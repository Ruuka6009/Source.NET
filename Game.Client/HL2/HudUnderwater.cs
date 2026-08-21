using Game.Client.HUD;
using Game.Shared;

using Source;
using Source.Common.GUI;
using Source.Common.MaterialSystem;
using Source.GUI.Controls;

namespace Game.Client.HL2;

/// <summary>
/// Tints the screen while the player's eyes are under water, and runs a short drain-off when they
/// surface.
///
/// This is the overlay half of the underwater look. Warping the rendered scene the way Source does
/// needs a copy of the backbuffer to sample from, and this port has no CopyRenderTargetToTexture,
/// so the distortion is not possible yet - see VULKAN_TODO.md.
/// </summary>
[DeclareHudElement(Name = "CHudUnderwater")]
class HudUnderwater : EditableHudElement, IHudElement
{
	[PanelAnimationVar("WaterColor", "26 70 84 150")] protected Color WaterColor;
	[PanelAnimationVar("DeepColor", "12 38 52 190")] protected Color DeepColor;

	/// <summary>0 when dry, 1 when fully submerged; eased so the transition is not a hard cut.</summary>
	float submerged;
	/// <summary>Counts down after surfacing while the sheet of water runs off the screen.</summary>
	float drain;

	const float DrainSeconds = 0.9f;

	long lastTicks;

	public HudUnderwater(string? panelName) : base(null, "HudUnderwater") {
		SetParent(clientMode.GetViewport());
		((IHudElement)this).SetHiddenBits(0);
		lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
	}

	public void Reset() {
		submerged = 0;
		drain = 0;
	}

	public bool ShouldDraw() {
		return (submerged > 0.001f || drain > 0.0f) && IHudElement.DefaultShouldDraw(this);
	}

	public override void OnThink() {
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		float dt = (float)((now - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
		lastTicks = now;
		dt = Math.Clamp(dt, 0.0f, 0.1f);

		BasePlayer? player = BasePlayer.GetLocalPlayer();
		bool eyesUnder = player != null && player.GetWaterLevel() >= WaterLevel.Eyes;

		float previous = submerged;

		// Going under is quick, coming up is quicker still - that asymmetry is what makes
		// surfacing feel like breaking through rather than fading out.
		float target = eyesUnder ? 1.0f : 0.0f;
		float rate = eyesUnder ? 6.0f : 12.0f;
		submerged += (target - submerged) * Math.Clamp(dt * rate, 0.0f, 1.0f);

		// Just surfaced: start the run-off.
		if (previous > 0.5f && submerged <= 0.5f && !eyesUnder)
			drain = DrainSeconds;

		if (drain > 0.0f)
			drain = Math.Max(0.0f, drain - dt);
	}

	ITexture? frameCopy;

	public override void Paint() {
		GetSize(out int wide, out int tall);

		if (submerged > 0.001f) {
			// Grab the frame as it stands (world and viewmodel are already drawn, the rest of the
			// HUD is not) into _rt_FullFrameFB. Nothing samples it yet - the distortion pass still
			// needs its shader - but this is the path screen-space effects will use.
			frameCopy ??= materials.FindTexture(MaterialDefines.FULL_FRAME_FRAMEBUFFER, null, false);
			if (frameCopy != null) {
				using MatRenderContextPtr renderContext = new(materials);
				renderContext.CopyRenderTargetToTexture(frameCopy);
			}
		}

		if (submerged > 0.001f) {
			// Deeper tint low on the screen, lighter toward the surface above, drawn as a few
			// bands rather than one flat fill so there is some sense of depth to it.
			const int bands = 8;
			for (int i = 0; i < bands; i++) {
				float t = i / (float)(bands - 1);
				Color band = Lerp(WaterColor, DeepColor, t);
				band = WithAlpha(band, (byte)(band.A * submerged));

				int y0 = tall * i / bands;
				int y1 = tall * (i + 1) / bands;
				Surface.DrawSetColor(band);
				Surface.DrawFilledRect(0, y0, wide, y1);
			}
		}

		if (drain > 0.0f) {
			// A sheet of water sliding off the screen: the covered band shrinks from the bottom
			// up while it fades, which reads as the water level dropping past the eyes.
			float progress = 1.0f - (drain / DrainSeconds);
			int bottom = (int)(tall * (1.0f - progress));

			Color sheet = WaterColor;
			sheet = WithAlpha(sheet, (byte)(sheet.A * (1.0f - progress) * 0.85f));
			Surface.DrawSetColor(sheet);
			Surface.DrawFilledRect(0, 0, wide, bottom);

			// A brighter lip at the waterline as it passes down the screen.
			Color lip = WaterColor;
			lip = WithAlpha(lip, (byte)(200 * (1.0f - progress)));
			Surface.DrawSetColor(lip);
			Surface.DrawFilledRect(0, Math.Max(0, bottom - 3), wide, bottom);
		}
	}

	static Color WithAlpha(in Color c, byte alpha) => new(c.R, c.G, c.B, alpha);

	static Color Lerp(in Color a, in Color b, float t) => new(
		(byte)(a.R + (b.R - a.R) * t),
		(byte)(a.G + (b.G - a.G) * t),
		(byte)(a.B + (b.B - a.B) * t),
		(byte)(a.A + (b.A - a.A) * t));

	public override void ApplySchemeSettings(IScheme scheme) {
		base.ApplySchemeSettings(scheme);
		SetPaintBackgroundEnabled(false);

		surface.GetFullscreenViewport(out _, out _, out int wide, out int tall);
		SetSize(wide, tall);
	}
}
