package com.stealthmode;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanFilter;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelUuid;
import android.os.SystemClock;
import android.util.Log;

import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.Collections;
import java.util.UUID;
import java.util.concurrent.ConcurrentLinkedQueue;

/**
 * Quest side of the Bluetooth link to the Mac relay (mac/whoop_relay.py), so the game does not depend on the venue Wi-Fi.
 * Finds the Mac's "StealthRelay" GATT service, connects, and keeps the link up (retrying on any failure).
 *   send(json)  queues a message; it is written to the Mac one at a time (write without response)
 *   sendAudio(base64)  the same queue for a binary frame of the Engineer's voice
 *   poll()      returns the next message the Mac sent (heart rate, acks) or null
 * Called from Unity (HeartLink.cs) with an AndroidJavaObject; everything runs on the main looper, no callbacks into C#.
 */
public class BleLink {
    static final String T = "StealthBle";
    static final UUID SVC = UUID.fromString("5a1e0001-6a7e-4b1c-9d2f-2b6f5ad5e001");
    static final UUID TO_MAC = UUID.fromString("5a1e0002-6a7e-4b1c-9d2f-2b6f5ad5e001");
    static final UUID TO_QUEST = UUID.fromString("5a1e0003-6a7e-4b1c-9d2f-2b6f5ad5e001");
    static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");

    private final Context ctx;
    private final Handler h = new Handler(Looper.getMainLooper());
    private final ConcurrentLinkedQueue<String> inbox = new ConcurrentLinkedQueue<>();
    private final ArrayDeque<byte[]> outbox = new ArrayDeque<>();

    private BluetoothLeScanner scanner;
    private ScanCallback scanCb;
    private BluetoothGatt gatt;
    private BluetoothGattCharacteristic toMac;
    private BluetoothDevice device;
    private volatile boolean running, connected;
    private boolean writing;
    private long writeStarted;
    private int mtu = 23;
    private volatile String status = "idle";

    public BleLink(Context context) { ctx = context.getApplicationContext() != null ? context.getApplicationContext() : context; }

    public void start() { running = true; h.post(this::scan); h.post(tick); }

    public void stop() { running = false; h.post(() -> { stopScan(); closeGatt(); }); }

    public boolean isConnected() { return connected; }

    public String getStatus() { return status; }

    /** Next message from the Mac, or null. */
    public String poll() { return inbox.poll(); }

    /** Queue a message for the Mac. Dropped oldest-first if the link is down for a long time. */
    public void send(String json) {
        byte[] b = json.getBytes(StandardCharsets.UTF_8);
        if (b.length > mtu - 3 && mtu > 23) Log.w(T, "message of " + b.length + " B exceeds the " + (mtu - 3) + " B packet");
        queue(b);
    }

    public void sendAudio(String base64) { queue(android.util.Base64.decode(base64, android.util.Base64.NO_WRAP)); }

    private void queue(byte[] b) {
        synchronized (outbox) {
            if (outbox.size() > 300) outbox.pollFirst();
            outbox.addLast(b);
        }
        h.post(this::pump);
    }

    // ------------------------------------------------------------------ connecting
    private boolean hasPermissions() {
        if (Build.VERSION.SDK_INT < 31) return true;
        return ctx.checkSelfPermission("android.permission.BLUETOOTH_SCAN") == PackageManager.PERMISSION_GRANTED
            && ctx.checkSelfPermission("android.permission.BLUETOOTH_CONNECT") == PackageManager.PERMISSION_GRANTED;
    }

    private void retryScan(long delayMs) { if (running) h.postDelayed(this::scan, delayMs); }

    private void scan() {
        if (!running || connected || scanCb != null || gatt != null) return;
        if (!hasPermissions()) { status = "waiting for Bluetooth permission"; retryScan(2000); return; }
        BluetoothManager m = (BluetoothManager) ctx.getSystemService(Context.BLUETOOTH_SERVICE);
        BluetoothAdapter a = m == null ? null : m.getAdapter();
        if (a == null || !a.isEnabled()) { status = "Bluetooth is off"; retryScan(2000); return; }
        scanner = a.getBluetoothLeScanner();
        if (scanner == null) { status = "no LE scanner"; retryScan(2000); return; }
        status = "scanning for the Mac";
        scanCb = new ScanCallback() {
            @Override public void onScanResult(int type, ScanResult r) {
                stopScan();
                device = r.getDevice();
                Log.i(T, "found the Mac " + device.getAddress() + " rssi " + r.getRssi());
                connect();
            }
            @Override public void onScanFailed(int e) { Log.w(T, "scan failed " + e); scanCb = null; status = "scan failed " + e; retryScan(2000); }
        };
        ScanFilter f = new ScanFilter.Builder().setServiceUuid(new ParcelUuid(SVC)).build();
        ScanSettings s = new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build();
        try { scanner.startScan(Collections.singletonList(f), s, scanCb); }
        catch (Exception e) { Log.w(T, "startScan: " + e); scanCb = null; retryScan(2000); }
    }

    private void stopScan() {
        if (scanner != null && scanCb != null) { try { scanner.stopScan(scanCb); } catch (Exception ignored) { } }
        scanCb = null;
    }

    private void connect() {
        status = "connecting";
        long myAttempt = SystemClock.elapsedRealtime();
        gatt = device.connectGatt(ctx, false, cb, BluetoothDevice.TRANSPORT_LE, BluetoothDevice.PHY_LE_1M_MASK, h);
        h.postDelayed(() -> { if (running && !connected && gatt != null && SystemClock.elapsedRealtime() - myAttempt >= 9000) { Log.w(T, "connect timeout"); dropAndRetry(); } }, 10000);
    }

    private void closeGatt() {
        connected = false; writing = false;
        if (gatt != null) { try { gatt.disconnect(); } catch (Exception ignored) { } try { gatt.close(); } catch (Exception ignored) { } gatt = null; }
        toMac = null;
    }

    private void dropAndRetry() { closeGatt(); stopScan(); status = "reconnecting"; retryScan(800); }

    private final BluetoothGattCallback cb = new BluetoothGattCallback() {
        @Override public void onConnectionStateChange(BluetoothGatt g, int st, int state) {
            Log.i(T, "connection status=" + st + " state=" + state);
            if (state == BluetoothProfile.STATE_CONNECTED && st == 0) { g.requestMtu(247); return; }
            dropAndRetry();     // status 133 and friends: generic, a retry clears them
        }
        @Override public void onMtuChanged(BluetoothGatt g, int m, int st) {
            mtu = m;
            try { g.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH); } catch (Exception ignored) { }
            g.discoverServices();
        }
        @Override public void onServicesDiscovered(BluetoothGatt g, int st) {
            BluetoothGattService s = g.getService(SVC);
            if (s == null) { Log.w(T, "relay service missing"); dropAndRetry(); return; }
            toMac = s.getCharacteristic(TO_MAC);
            BluetoothGattCharacteristic n = s.getCharacteristic(TO_QUEST);
            g.setCharacteristicNotification(n, true);
            BluetoothGattDescriptor d = n.getDescriptor(CCCD);
            d.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
            g.writeDescriptor(d);
        }
        @Override public void onDescriptorWrite(BluetoothGatt g, BluetoothGattDescriptor d, int st) {
            connected = st == 0; status = connected ? "connected (mtu " + mtu + ")" : "subscribe failed " + st;
            Log.i(T, status);
            if (connected) h.post(BleLink.this::pump); else dropAndRetry();
        }
        @Override public void onCharacteristicWrite(BluetoothGatt g, BluetoothGattCharacteristic c, int st) { writing = false; h.post(BleLink.this::pump); }
        @Override public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic c, byte[] v) { inbox.add(new String(v, StandardCharsets.UTF_8)); }
        @SuppressWarnings("deprecation")
        @Override public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic c) {
            if (Build.VERSION.SDK_INT < 33) inbox.add(new String(c.getValue(), StandardCharsets.UTF_8));   // 33+ delivers the byte[] overload above
        }
    };

    // ------------------------------------------------------------------ writing: strictly one at a time
    private final Runnable tick = new Runnable() {
        @Override public void run() {
            if (!running) return;
            if (writing && SystemClock.elapsedRealtime() - writeStarted > 400) writing = false;   // a lost write callback must not stall the queue
            pump();
            h.postDelayed(this, 100);
        }
    };

    @SuppressWarnings("deprecation")
    private void pump() {
        if (!connected || writing || gatt == null || toMac == null) return;
        byte[] b;
        synchronized (outbox) { b = outbox.pollFirst(); }
        if (b == null) return;
        writing = true; writeStarted = SystemClock.elapsedRealtime();
        boolean ok;
        try {
            if (Build.VERSION.SDK_INT >= 33) ok = gatt.writeCharacteristic(toMac, b, BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE) == 0;
            else { toMac.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE); toMac.setValue(b); ok = gatt.writeCharacteristic(toMac); }
        } catch (Exception e) { ok = false; }
        if (!ok) {
            writing = false;
            synchronized (outbox) { outbox.addFirst(b); }   // keep it, try again shortly
            h.postDelayed(this::pump, 60);
        }
    }
}
