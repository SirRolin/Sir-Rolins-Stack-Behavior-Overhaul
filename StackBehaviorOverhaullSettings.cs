using System;
using Verse;

namespace StackBehaviorOverhaul
{
    public class StackBehaviorOverhaulSettings : ModSettings
    {
        private const float DefaultExplosionScaling = 7.5f;
        private const float MinExplosionScaling = 0.1f;
        private const float MaxExplosionScaling = 100f;

        private float explosionScaling = DefaultExplosionScaling;

        internal static StackBehaviorOverhaulSettings Current = new StackBehaviorOverhaulSettings();

        public float ExplosionScaling
        {
            get { return explosionScaling; }
            set { explosionScaling = value; }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref explosionScaling, "explosionScaling", DefaultExplosionScaling);
            explosionScaling = ClampAndRound(explosionScaling);
            base.ExposeData();
        }

        internal static float ClampAndRound(float value)
        {
            float clamped = Math.Max(MinExplosionScaling, Math.Min(MaxExplosionScaling, value));
            return (float)Math.Round(clamped * 10f, MidpointRounding.AwayFromZero) / 10f;
        }
    }

    public class StackBehaviorOverhaul : Mod
    {
        private readonly StackBehaviorOverhaulSettings settings;

        public StackBehaviorOverhaul(ModContentPack content) : base(content)
        {
            settings = GetSettings<StackBehaviorOverhaulSettings>();
            StackBehaviorOverhaulSettings.Current = settings;
        }

        public override string SettingsCategory()
        {
            return "Stack Behaviour Overhaul";
        }

        public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("Explosion scaling: " + settings.ExplosionScaling.ToString("0.0"));
            settings.ExplosionScaling = StackBehaviorOverhaulSettings.ClampAndRound(
                listing.Slider(settings.ExplosionScaling, 0.1f, 100f));
            listing.Label("At 7.5, a stack of 10 out of 75 takes full explosion damage.");
            listing.End();
        }
    }
}
