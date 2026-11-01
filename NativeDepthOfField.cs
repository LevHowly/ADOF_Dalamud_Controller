using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace ADOF_Dalamud_Controller;

public sealed unsafe class NativeDepthOfField : IDisposable
{
    private readonly IFramework framework;
    private readonly Hook<Manager.Delegates.Render> hook;
    public bool Suppress { get; set; }
    public string? Error { get; private set; }

    public NativeDepthOfField(IGameInteropProvider interop, IFramework framework)
    {
        this.framework = framework;
        hook = interop.HookFromAddress<Manager.Delegates.Render>(
            Manager.MemberFunctionPointers.Render, Render);
        try
        {
            hook.Enable();
        }
        catch
        {
            hook.Dispose();
            throw;
        }
    }

    private void Render(Manager* renderManager)
    {
        if (!Suppress || !framework.IsInFrameworkUpdateThread)
        {
            hook.Original(renderManager);
            return;
        }
        var effects = PostEffectManager.Instance();
        if (effects == null)
        {
            Error = "Native depth-of-field resources are unavailable; the game's blur has not been suppressed.";
            hook.Original(renderManager);
            return;
        }
        Error = null;
        var enabled = (effects->Flags & PostEffectFlags.DepthOfField) != 0;
        effects->Flags &= ~PostEffectFlags.DepthOfField;
        try
        {
            hook.Original(renderManager);
        }
        finally
        {
            // Restore only the bit we changed, preserving all other render settings.
            if (enabled && PostEffectManager.Instance() == effects)
                effects->Flags |= PostEffectFlags.DepthOfField;
        }
    }

    public void Dispose()
    {
        Suppress = false;
        hook.Dispose();
    }
}
