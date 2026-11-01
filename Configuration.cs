using Dalamud.Configuration;

namespace ADOF_Dalamud_Controller;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool DeactivateOnExit { get; set; } = true;
    public bool ActivateAdof { get; set; }
    public bool BlurVisualizerEnabled { get; set; }
    public bool CameraGridWhiteLines { get; set; } = true;
    public bool AutofocusEnabled { get; set; }
    public float MaxBlurRadiusPixels { get; set; } = 24f;
    public float BlurStrength { get; set; } = 8f;
    public float ApertureFNumber { get; set; } = 2.8f;
    public float FocusDistanceMeters { get; set; } = 5f;
    public float NearFocusMeters { get; set; } = 4.286989f;
    public float FarFocusMeters { get; set; } = 5.997505f;
    public bool FarFocusInfinite { get; set; }
}
