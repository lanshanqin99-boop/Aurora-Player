using Windows.UI;

namespace Aurora;

/// <summary>
/// Derives an <see cref="AmbientPalette"/> from a cover image.
///
/// The pipeline is fixed and deliberately simple to reason about:
/// <list type="number">
/// <item><b>Scaled sampling.</b> The cover is decoded once and scaled down by the decoder itself
/// (48 px on the longest edge, see <see cref="CoverPixelSampler"/>), so a 3000×3000 scan costs the
/// same as a 300×300 one. Only those samples are ever examined.</item>
/// <item><b>HSL per pixel.</b> Every sample is converted from sRGB to HSL, because hue,
/// saturation and lightness are what an album's colour actually is: two covers a shade apart in
/// brightness share a hue, two covers a shade apart in RGB share nothing. Fully transparent
/// pixels are skipped — they are not part of the artwork and must not vote.</item>
/// <item><b>Count the dominant colours.</b> Chromatic pixels vote into 24 hue bins (15° each),
/// weighted purely by area; neutral pixels go into a separate tone tally. Black and grey are
/// legitimate outcomes, not failures: a monochrome sleeve produces a monochrome ambience.</item>
/// <item><b>Produce a dark colour.</b> The winning colour is turned back into RGB as a dark
/// version of itself — same hue, saturation pulled down the way real surfaces lose chroma in
/// shadow, lightness clamped into a band that can fill a window without glaring — then lifted
/// until its brightest channel is actually visible on screen.</item>
/// </list>
///
/// Anything that goes wrong is <see cref="AmbientPalette.Black"/>: no cover, a damaged image, an
/// unknown format, a fully transparent image. A missing colour must never affect playback.
/// </summary>
internal static class PaletteExtractor
{
    // ---- Counting constants -----------------------------------------------

    /// <summary>Hue resolution: 24 bins of 15°. Enough to separate neighbouring keys on a sleeve.</summary>
    private const int HueBins = 24;

    /// <summary>
    /// Below this saturation a pixel carries no usable hue and counts as neutral. Chosen so that
    /// JPEG colour noise around a grey or black field cannot manufacture a hue.
    /// </summary>
    private const double NeutralSaturation = 0.12;

    /// <summary>
    /// Share of the cover that must be coloured before the palette is built from a hue. Below it
    /// the sleeve is treated as monochrome, which is what a black, white or silver cover is.
    /// </summary>
    private const double ChromaticShareFloor = 0.15;

    /// <summary>
    /// Share of the winning bin's mass a second, differently-hued bin needs in order to become the
    /// gradient's partner colour. Stops a logo or a single flower from setting the second colour.
    /// </summary>
    private const double PartnerShare = 0.15;

    /// <summary>Hue distance, in bins, before a bin counts as a different colour.</summary>
    private const int PartnerHueDistance = 2;

    // ---- Output shaping constants -----------------------------------------

    /// <summary>
    /// Deepest and brightest the background may end up. The floor keeps a hue present at all; the
    /// ceiling keeps the result a background rather than a wash of primary colour.
    /// </summary>
    private const double MinBackground = 0.055;
    private const double MaxBackground = 0.34;

    /// <summary>
    /// Depth floor for the secondary only. Deliberately below <see cref="MinBackground"/> so the
    /// gradient's shadow end stays distinguishable from its main stop on very dark covers.
    /// </summary>
    private const double MinSecondary = 0.018;

    /// <summary>Saturation ceilings, and how far below the source's lightness the background sits.</summary>
    private const double MaxBackgroundSaturation = 0.55;
    private const double MaxSecondarySaturation = 0.45;
    private const double BackgroundDepth = 0.55;

    /// <summary>
    /// Smallest value the brightest channel of a <em>coloured</em> result may end up with. Below
    /// roughly this a saturated hue is present in the numbers but reads as black on screen, which
    /// is the difference between a deep sleeve showing its colour and showing nothing. Scaling all
    /// three channels by one factor preserves the hue exactly, because hue is their ratio.
    /// </summary>
    private const double MinChannel = 0.16;

    /// <summary>Neutral output band: pure black through a workable dark grey.</summary>
    private const double MinNeutral = 0.02;
    private const double MaxNeutral = 0.40;

    /// <summary>
    /// Analyses an already-decoded sample. Exposed so the service can run decode and analysis on
    /// one background thread instead of two.
    /// </summary>
    internal static AmbientPalette Extract(PixelSample sample)
    {
        if (!sample.IsUsable)
        {
            return AmbientPalette.Black;
        }

        Tally tally = Count(sample);
        if (tally.Total == 0)
        {
            // Nothing opaque: there is no artwork to take a colour from.
            return AmbientPalette.Black;
        }

        double chromaticShare = (double)tally.Chromatic / tally.Total;
        HueBin? dominant = tally.Dominant;

        if (dominant is null || chromaticShare < ChromaticShareFloor)
        {
            // A black, white or silver sleeve. The dominant tone of its neutral pixels is the
            // honest answer, so a monochrome cover yields a monochrome background rather than an
            // invented tint.
            return Neutral(tally);
        }

        // The gradient's second stop: a genuinely different hue when one holds enough of the
        // cover, otherwise a deeper step of the same hue.
        HueBin partner = FindPartner(tally, dominant) ?? dominant;
        bool sameHue = ReferenceEquals(partner, dominant);

        Color background = Darken(
            dominant.Hue, dominant.Saturation, dominant.Lightness,
            MaxBackgroundSaturation, BackgroundDepth, lift: true);

        // The secondary is the gradient's shadow end and is deliberately not lifted to the
        // visibility floor: lifting both ends of a very dark cover pinned them to the same value,
        // so the gradient disappeared exactly where it is most needed. The floor below protects
        // the value a large surface must reach; the secondary is a smaller field of that surface.
        Color secondary = Darken(
            partner.Hue, partner.Saturation, partner.Lightness,
            MaxSecondarySaturation, sameHue ? 0.30 : 0.45, lift: false);

        // The accent is the only value allowed to be bright: it is for small highlights.
        Color accent = Darken(dominant.Hue, dominant.Saturation, dominant.Lightness, 0.85, 1.15, lift: true);

        return new AmbientPalette(background, secondary, accent);
    }

    /// <summary>
    /// Converts every opaque sample to HSL and counts it. Chromatic pixels vote into a hue bin;
    /// neutral ones contribute to the tone tally only.
    /// </summary>
    private static Tally Count(PixelSample sample)
    {
        var tally = new Tally();

        for (int i = 0; i < sample.PixelCount; i++)
        {
            Color? pixel = sample.PixelAt(i);
            if (pixel is not Color colour)
            {
                // Fully transparent: not part of the artwork, so it does not vote.
                continue;
            }

            tally.Total++;

            (double h, double s, double l) =
                ToHsl(colour.R / 255.0, colour.G / 255.0, colour.B / 255.0);

            if (s < NeutralSaturation)
            {
                // Black, grey or white. Counted separately so the neutral tone can be reported
                // when the cover has no colour worth speaking for.
                tally.Neutral++;
                tally.NeutralLightness += l;
                continue;
            }

            tally.Chromatic++;

            int index = (int)(h * HueBins);
            if (index >= HueBins)
            {
                index = HueBins - 1;
            }
            else if (index < 0)
            {
                index = 0;
            }

            HueBin bin = tally.Bin(index);
            bin.Add(h, s, l);

            if (tally.Dominant is null || bin.Count > tally.Dominant.Count)
            {
                tally.Dominant = bin;
            }
        }

        return tally;
    }

    /// <summary>
    /// The most-populated bin far enough from the dominant hue to read as a different colour and
    /// large enough to be intentional rather than a stray highlight. Null when there is none.
    /// </summary>
    private static HueBin? FindPartner(Tally tally, HueBin dominant)
    {
        HueBin? partner = null;

        foreach (HueBin bin in tally.Populated)
        {
            if (ReferenceEquals(bin, dominant))
            {
                continue;
            }

            int distance = Math.Abs(bin.Index - dominant.Index);
            distance = Math.Min(distance, HueBins - distance);

            if (distance < PartnerHueDistance || bin.Count < dominant.Count * PartnerShare)
            {
                continue;
            }

            if (partner is null || bin.Count > partner.Count)
            {
                partner = bin;
            }
        }

        return partner;
    }

    /// <summary>
    /// Palette for a cover with no meaningful hue. The dominant neutral tone sets the depth, so a
    /// black sleeve stays near black — a real result — while a white sleeve becomes a dark grey.
    /// </summary>
    private static AmbientPalette Neutral(Tally tally)
    {
        if (tally.Neutral == 0)
        {
            return AmbientPalette.Black;
        }

        double tone = tally.NeutralLightness / tally.Neutral;

        double background = Math.Clamp(0.04 + (tone * 0.5), MinNeutral, MaxNeutral);
        double secondary = Math.Clamp(background * 0.45, 0.01, MaxNeutral);
        double accent = Math.Clamp(background + 0.42, 0.50, 0.92);

        return new AmbientPalette(Grey(background), Grey(secondary), Grey(accent));
    }

    private static Color Grey(double level)
    {
        byte channel = (byte)Math.Clamp(Math.Round(level * 255), 0, 255);
        return Color.FromArgb(0xFF, channel, channel, channel);
    }

    /// <summary>
    /// Turns a colour into a workable dark surface colour: the hue is kept exactly, saturation is
    /// capped and reduced with depth, and the lightness is placed in the background band relative
    /// to the source's own lightness — so a dark sleeve stays dark and a mid sleeve stays mid,
    /// instead of every cover collapsing to the same near-black.
    ///
    /// When <paramref name="lift"/> is set, the result is raised until its brightest channel
    /// reaches <see cref="MinChannel"/>. That applies to coloured results only: a genuinely black
    /// or grey source must be allowed to stay black, which is why the neutral path does not come
    /// through here.
    /// </summary>
    private static Color Darken(
        double hue,
        double saturation,
        double sourceLightness,
        double maxSaturation,
        double depth,
        bool lift)
    {
        double floor = lift ? MinBackground : MinSecondary;
        double target = Math.Clamp(sourceLightness * depth, floor, MaxBackground);

        // Chroma falls away as the colour darkens, the way a real surface loses colour in shadow.
        double saturationCap = maxSaturation * Math.Clamp(0.35 + (0.85 * target), 0, 1);
        double capped = Math.Min(saturation, saturationCap);

        (double r, double g, double b) = FromHsl(hue, capped, target);

        if (lift)
        {
            double brightest = Math.Max(r, Math.Max(g, b));
            if (brightest > 0 && brightest < MinChannel)
            {
                double scale = MinChannel / brightest;
                r = Math.Min(1, r * scale);
                g = Math.Min(1, g * scale);
                b = Math.Min(1, b * scale);
            }
        }

        return ToColor(r, g, b);
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double delta = max - min;

        if (delta <= 0)
        {
            return (0, 0, l);
        }

        double s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);

        double h;
        if (max == r)
        {
            h = ((g - b) / delta) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = ((b - r) / delta) + 2;
        }
        else
        {
            h = ((r - g) / delta) + 4;
        }

        return (h / 6, s, l);
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            return (l, l, l);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        double p = (2 * l) - q;

        return (
            HueToChannel(p, q, h + (1.0 / 3)),
            HueToChannel(p, q, h),
            HueToChannel(p, q, h - (1.0 / 3)));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        if (t < 2.0 / 3)
        {
            return p + ((q - p) * ((2.0 / 3) - t) * 6);
        }

        return p;
    }

    private static Color ToColor(double r, double g, double b) => Color.FromArgb(
        0xFF,
        (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(b * 255), 0, 255));

    /// <summary>
    /// One hue bin: how many pixels landed in it and their average saturation and lightness. The
    /// hue is averaged as an offset from the bin's own centre, so a bin needs no wrap-around
    /// arithmetic and a cover whose dominant hue straddles 0°/360° still averages correctly.
    /// </summary>
    private sealed class HueBin
    {
        public HueBin(int index) => Index = index;

        public int Index { get; }

        public int Count { get; private set; }

        private double _hueOffset;
        private double _saturation;
        private double _lightness;

        /// <summary>Centre of this bin, 0..1.</summary>
        private double Centre => (Index + 0.5) / HueBins;

        public double Hue
        {
            get
            {
                if (Count == 0)
                {
                    return Centre;
                }

                double hue = Centre + (_hueOffset / Count);
                return hue < 0 ? hue + 1 : hue >= 1 ? hue - 1 : hue;
            }
        }

        public double Saturation => Count == 0 ? 0 : _saturation / Count;

        public double Lightness => Count == 0 ? 0 : _lightness / Count;

        public void Add(double hue, double saturation, double lightness)
        {
            double offset = hue - Centre;
            if (offset > 0.5)
            {
                offset -= 1;
            }
            else if (offset < -0.5)
            {
                offset += 1;
            }

            _hueOffset += offset;
            _saturation += saturation;
            _lightness += lightness;
            Count++;
        }
    }

    /// <summary>The counting pass's result: the hue bins and the neutral totals.</summary>
    private sealed class Tally
    {
        private readonly HueBin?[] _bins = new HueBin?[HueBins];

        public int Total { get; set; }

        public int Chromatic { get; set; }

        public int Neutral { get; set; }

        public double NeutralLightness { get; set; }

        public HueBin? Dominant { get; set; }

        public HueBin Bin(int index) => _bins[index] ??= new HueBin(index);

        /// <summary>The populated bins, in index order.</summary>
        public IEnumerable<HueBin> Populated
        {
            get
            {
                foreach (HueBin? bin in _bins)
                {
                    if (bin is not null)
                    {
                        yield return bin;
                    }
                }
            }
        }
    }
}
