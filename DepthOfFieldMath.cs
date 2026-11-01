namespace ADOF_Dalamud_Controller;

public static class DepthOfFieldMath
{
    public const double FocalLengthMeters = 0.05;
    public const double CircleOfConfusionMeters = 0.00003;
    public const double ProjectionNearPlaneMeters = 0.1;
    public const double ProjectionFarPlaneMeters = 1000;
    public const double LinearizationFarPlane = 1000;
    public const double AcceptableBlurRadiusPixels = 0.5;

    public static double BoostBlurRadius(double signedRadius, double strength, double maximumRadius)
    {
        var magnitude = Math.Abs(signedRadius);
        var boosted = Math.Min(magnitude, AcceptableBlurRadiusPixels) +
                      Math.Max(0, magnitude - AcceptableBlurRadiusPixels) * strength;
        return Math.CopySign(Math.Min(Math.Max(boosted - 1.0, 0), maximumRadius), signedRadius);
    }

    public static bool TryFromCamera(
        double aperture,
        double focusDistance,
        out double nearFocus,
        out double farFocus)
    {
        nearFocus = 0;
        farFocus = 0;

        if (!double.IsFinite(aperture) || aperture <= 0 ||
            !double.IsFinite(focusDistance) || focusDistance <= FocalLengthMeters)
            return false;

        var focalLength = FocalLengthMeters;
        var hyperfocal = (focalLength * focalLength) /
                         (aperture * CircleOfConfusionMeters);
        var nearDenominator = hyperfocal + focusDistance - focalLength;
        var farDenominator = hyperfocal - focusDistance + focalLength;

        if (!double.IsFinite(hyperfocal) || nearDenominator <= 0)
            return false;

        nearFocus = hyperfocal * focusDistance / nearDenominator;
        farFocus = farDenominator <= 0
            ? double.PositiveInfinity
            : hyperfocal * focusDistance / farDenominator;
        return double.IsFinite(nearFocus) && nearFocus > 0 &&
               (double.IsFinite(farFocus) || double.IsPositiveInfinity(farFocus));
    }

    public static bool TryFromDepthLimits(
        double nearFocus,
        double farFocus,
        out double aperture,
        out double focusDistance)
    {
        aperture = 0;
        focusDistance = 0;

        if (!double.IsFinite(nearFocus) ||
            (!double.IsFinite(farFocus) && !double.IsPositiveInfinity(farFocus)) ||
            nearFocus <= FocalLengthMeters || farFocus <= nearFocus)
            return false;

        var focalLength = FocalLengthMeters;
        var calculatedFocusDistance = double.IsPositiveInfinity(farFocus)
            ? 2 * nearFocus
            : 2 * nearFocus / (1 + nearFocus / farFocus);
        var hyperfocal = nearFocus * (calculatedFocusDistance - focalLength) /
                         (calculatedFocusDistance - nearFocus);
        var apertureDenominator = CircleOfConfusionMeters * hyperfocal;

        if (hyperfocal <= 0 ||
            apertureDenominator <= 0)
            return false;

        aperture = (focalLength * focalLength) / apertureDenominator;
        focusDistance = calculatedFocusDistance;
        return double.IsFinite(aperture) && aperture > 0 &&
               double.IsFinite(focusDistance) && focusDistance > focalLength;
    }

    public static double ToLinearDepth(double distanceMeters)
    {
        var distance = Math.Clamp(distanceMeters, ProjectionNearPlaneMeters, ProjectionFarPlaneMeters);
        var projected = ProjectionFarPlaneMeters / (ProjectionFarPlaneMeters - ProjectionNearPlaneMeters) *
                        (1 - ProjectionNearPlaneMeters / distance);
        return projected / (LinearizationFarPlane - projected * (LinearizationFarPlane - 1));
    }

    public static bool TryGetShaderParameters(
        double aperture, double focusDistance, double maxBlurRadius,
        out ShaderParameters parameters)
    {
        parameters = default;
        if (focusDistance < ProjectionNearPlaneMeters || focusDistance > ProjectionFarPlaneMeters ||
            !double.IsFinite(maxBlurRadius) || maxBlurRadius < 1 || maxBlurRadius > 100 ||
            !TryFromCamera(aperture, focusDistance, out _, out _))
            return false;

        var lensCoefficient = AcceptableBlurRadiusPixels * FocalLengthMeters * FocalLengthMeters /
                              (aperture * CircleOfConfusionMeters);
        var bias = lensCoefficient / (focusDistance - FocalLengthMeters);
        var scale = -bias * focusDistance;
        if (!float.IsFinite((float)bias) || !float.IsFinite((float)scale) ||
            !float.IsFinite((float)lensCoefficient))
            return false;

        var a = 1 / ProjectionNearPlaneMeters;
        var denominator = LinearizationFarPlane - 1;
        var b = a * denominator - LinearizationFarPlane *
                (ProjectionFarPlaneMeters - ProjectionNearPlaneMeters) /
                (ProjectionFarPlaneMeters * ProjectionNearPlaneMeters);
        parameters = new ShaderParameters(
            (float)Math.Sqrt(ToLinearDepth(focusDistance)),
            (float)bias, (float)scale, (float)lensCoefficient,
            (float)a, (float)b, (float)denominator);
        return true;
    }

    public readonly record struct ShaderParameters(
        float ManualFocusDepth, float CocBias, float CocScale, float LensCoefficient,
        float InverseDepthA, float InverseDepthB, float InverseDepthDenominator);
}
