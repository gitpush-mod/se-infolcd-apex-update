using Sandbox.ModAPI;
using System.Collections.Generic;
using VRage.Game.ModAPI;

namespace STGTieredBuildAndRepair
{
    /// <summary>
    /// Per-subtype hardcoded settings for the tiered Build And Repair blocks.
    ///
    /// Originally this mod had a single global ModSettings.xml that applied to all
    /// blocks. The tier ladder requires different capabilities per subtype (Base is
    /// Walk+Weld only; Upgraded adds Fly mode; Advanced unlocks grind + janitor +
    /// idle collect), so the global settings are now overlaid by these per-subtype
    /// values on every read that affects tier-specific behavior.
    ///
    /// Non-tier-specific settings (Range cap, MaxBackgroundTasks, sound volume, etc.)
    /// still come from the global Mod.Settings.Welder.
    /// </summary>
    public static class TierConfig
    {
        public sealed class TierSettings
        {
            public string TierName;
            public SearchModes AllowedSearchModes;
            public SearchModes SearchModeDefault;
            public WorkModes AllowedWorkModes;
            public WorkModes WorkModeDefault;
            public bool UseGrindJanitorFixed;
            public AutoGrindRelation UseGrindJanitorDefault;
            public AutoGrindRelation AllowedGrindJanitorRelations;
            public bool CollectIfIdleFixed;
            public bool CollectIfIdleDefault;
            public bool PriorityFixed;
            public bool CollectPriorityFixed;
        }

        // BUG-093 safeguard (carried over from upstream): AllowedGrindJanitorRelations
        // MUST NOT be 0 — a zero mask silently breaks the janitor on every BaR in the
        // world by clobbering per-block UseGrindJanitorOn settings. We always set the
        // default mask here even on tiers where janitor is locked off via
        // UseGrindJanitorFixed + empty UseGrindJanitorDefault.
        private const AutoGrindRelation DEFAULT_JANITOR_MASK =
            AutoGrindRelation.NoOwnership | AutoGrindRelation.Enemies | AutoGrindRelation.Neutral;

        private static readonly TierSettings _base = new TierSettings
        {
            TierName = "Base",
            AllowedSearchModes = SearchModes.Grids,
            SearchModeDefault = SearchModes.Grids,
            AllowedWorkModes = WorkModes.WeldOnly,
            WorkModeDefault = WorkModes.WeldOnly,
            UseGrindJanitorFixed = true,
            UseGrindJanitorDefault = 0,
            AllowedGrindJanitorRelations = DEFAULT_JANITOR_MASK,
            CollectIfIdleFixed = true,
            CollectIfIdleDefault = false,
            PriorityFixed = false,
            CollectPriorityFixed = true,
        };

        private static readonly TierSettings _upgraded = new TierSettings
        {
            TierName = "Upgraded",
            AllowedSearchModes = SearchModes.Grids | SearchModes.BoundingBox,
            SearchModeDefault = SearchModes.Grids,
            AllowedWorkModes = WorkModes.WeldOnly,
            WorkModeDefault = WorkModes.WeldOnly,
            UseGrindJanitorFixed = true,
            UseGrindJanitorDefault = 0,
            AllowedGrindJanitorRelations = DEFAULT_JANITOR_MASK,
            CollectIfIdleFixed = true,
            CollectIfIdleDefault = false,
            PriorityFixed = false,
            CollectPriorityFixed = true,
        };

        private static readonly TierSettings _advanced = new TierSettings
        {
            TierName = "Advanced",
            AllowedSearchModes = SearchModes.Grids | SearchModes.BoundingBox,
            SearchModeDefault = SearchModes.Grids,
            AllowedWorkModes = WorkModes.WeldBeforeGrind | WorkModes.GrindBeforeWeld
                | WorkModes.GrindIfWeldGetStuck | WorkModes.WeldOnly | WorkModes.GrindOnly,
            WorkModeDefault = WorkModes.WeldOnly,
            UseGrindJanitorFixed = false,
            UseGrindJanitorDefault = 0,
            AllowedGrindJanitorRelations = DEFAULT_JANITOR_MASK,
            CollectIfIdleFixed = false,
            CollectIfIdleDefault = false,
            PriorityFixed = false,
            CollectPriorityFixed = false,
        };

        private static readonly Dictionary<string, TierSettings> _bySubtype =
            new Dictionary<string, TierSettings>
            {
                { "STG_NanobotBaseLarge", _base },
                { "STG_NanobotUpgradedLarge", _upgraded },
                { "STG_NanobotUpgradedSmall", _upgraded },
                { "STG_NanobotAdvancedLarge", _advanced },
                { "STG_NanobotAdvancedSmall", _advanced },
            };

        public static TierSettings ForSubtype(string subtypeId)
        {
            TierSettings ts;
            if (subtypeId != null && _bySubtype.TryGetValue(subtypeId, out ts)) return ts;
            return _base;
        }

        public static TierSettings ForBlock(IMyTerminalBlock block)
        {
            return block != null ? ForSubtype(block.BlockDefinition.SubtypeId) : _base;
        }

        public static TierSettings ForBlock(IMyCubeBlock block)
        {
            return block != null ? ForSubtype(block.BlockDefinition.SubtypeId) : _base;
        }

        // ============================================================================
        // Gate helpers — encapsulate the bitmask checks used by terminal predicates.
        // These replace the closure-captured snapshots in Terminal.cs:164-187.
        // ============================================================================

        public static bool IsWeldingAllowed(IMyTerminalBlock block)
        {
            return (ForBlock(block).AllowedWorkModes & (WorkModes.WeldBeforeGrind
                | WorkModes.GrindBeforeWeld | WorkModes.GrindIfWeldGetStuck
                | WorkModes.WeldOnly)) != 0;
        }

        public static bool IsGrindingAllowed(IMyTerminalBlock block)
        {
            return (ForBlock(block).AllowedWorkModes & (WorkModes.WeldBeforeGrind
                | WorkModes.GrindBeforeWeld | WorkModes.GrindIfWeldGetStuck
                | WorkModes.GrindOnly)) != 0;
        }

        public static bool IsJanitorAllowed(IMyTerminalBlock block)
        {
            var ts = ForBlock(block);
            var grindingAllowed = (ts.AllowedWorkModes & (WorkModes.WeldBeforeGrind
                | WorkModes.GrindBeforeWeld | WorkModes.GrindIfWeldGetStuck
                | WorkModes.GrindOnly)) != 0;
            return grindingAllowed && ts.AllowedGrindJanitorRelations != 0 && !ts.UseGrindJanitorFixed;
        }

        public static bool IsJanitorAllowedForRelation(IMyTerminalBlock block, AutoGrindRelation rel)
        {
            return IsJanitorAllowed(block) && (ForBlock(block).AllowedGrindJanitorRelations & rel) != 0;
        }

        public static bool IsBoundingBoxSearchAllowed(IMyTerminalBlock block)
        {
            return (ForBlock(block).AllowedSearchModes & SearchModes.BoundingBox) != 0;
        }

        public static bool IsCollectIdleConfigurable(IMyTerminalBlock block)
        {
            return !ForBlock(block).CollectIfIdleFixed;
        }

        public static bool IsWeldPriorityConfigurable(IMyTerminalBlock block)
        {
            return !ForBlock(block).PriorityFixed;
        }

        public static bool IsCollectPriorityConfigurable(IMyTerminalBlock block)
        {
            return !ForBlock(block).CollectPriorityFixed;
        }
    }
}
