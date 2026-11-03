using UnityEngine;

namespace StealthMode
{
    /// <summary>Marks a body-part collider so gunfire knows whose body it hit and where.</summary>
    public class Hitbox : MonoBehaviour
    {
        public enum Part { Head, Torso, Limb }
        public Part part;
        public SoldierRig owner;
    }
}
