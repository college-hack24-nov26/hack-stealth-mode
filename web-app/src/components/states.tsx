export function Loading() {
  return (
    <div style={{ paddingTop: 26, display: "grid", gap: 14 }} aria-busy="true">
      <div className="skeleton" style={{ height: 120 }} />
      <div className="skeleton" style={{ height: 220 }} />
      <div className="skeleton" style={{ height: 160 }} />
    </div>
  );
}

export function Empty({ title, body }: { title: string; body: string }) {
  return (
    <div style={{ paddingTop: 60, maxWidth: 420 }}>
      <h1 className="display" style={{ fontSize: 40, margin: 0 }}>{title}</h1>
      <p className="muted">{body}</p>
    </div>
  );
}
