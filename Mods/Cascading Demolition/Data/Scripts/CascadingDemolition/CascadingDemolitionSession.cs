using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace CascadingDemolition
{
    /// <summary>
    /// Server-side session component that converts "armed warhead destroyed by
    /// another blast" into "armed warhead detonates BEFORE its block dies".
    ///
    /// Vanilla SE has the inverse problem: when warhead A's explosion destroys
    /// warhead B's block, B is removed via normal block-destruction without
    /// firing its own explosion. We hook the damage system to pre-empt that:
    /// if a damage event would kill an armed warhead, we call Detonate() first,
    /// then let the damage finalize. The detonation spawns its own explosion,
    /// which damages adjacent blocks — and any armed warheads in that radius
    /// get the same treatment, naturally cascading.
    ///
    /// No per-block components, no spatial scans on our side. The damage system
    /// is the spatial query and the trigger.
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class CascadingDemolitionSession : MySessionComponentBase
    {
        private bool _registered;

        public override void LoadData()
        {
            // Damage handlers are server-authoritative. Running on a client in MP
            // would just produce ghost detonations that don't match the server.
            if (!MyAPIGateway.Multiplayer.IsServer) return;

            if (MyAPIGateway.Session?.DamageSystem == null) return;

            // Priority 0 — runs alongside other handlers; we don't need to
            // out-prioritize anything since we don't mutate the damage info.
            MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(0, OnBeforeDamage);
            _registered = true;
        }

        protected override void UnloadData()
        {
            // IMyDamageSystem doesn't expose an unregister method. Handlers
            // are owned by the session and cleaned up when it ends.
            _registered = false;
        }

        private void OnBeforeDamage(object target, ref MyDamageInformation info)
        {
            // Hot path — every bullet, grinder hit, voxel collision, etc. comes
            // through here. Bail as fast as possible on the common case.
            var slim = target as IMySlimBlock;
            if (slim == null) return;

            var warhead = slim.FatBlock as IMyWarhead;
            if (warhead == null) return;

            // An unarmed warhead is just a block. Let it die normally.
            if (!warhead.IsArmed) return;

            // Already detonating — countdown started. Don't double-trigger.
            if (warhead.IsCountingDown) return;

            // Will this damage actually destroy the block? slim.Integrity is
            // the current remaining integrity. If incoming damage doesn't kill
            // it, no need to chain — let the block soak the hit and stay armed
            // for whatever's coming next.
            if (info.Amount < slim.Integrity) return;

            // Trigger the warhead. Detonate() fires the explosion immediately
            // (via DetonateRequest — synced to clients automatically by SE).
            // The damage that's about to apply will still finalize and remove
            // the block, but our explosion spawns first.
            warhead.Detonate();
        }
    }
}
