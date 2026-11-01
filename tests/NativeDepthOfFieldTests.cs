using ADOF_Dalamud_Controller;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

internal static unsafe class NativeDepthOfFieldTests
{
    public static void Run()
    {
        var effects = new PostEffectManager();
        PostEffectManager.Current = &effects;
        var framework = new TestFramework();
        var interop = new TestInterop();
        using var suppression = new NativeDepthOfField(interop, framework);
        var unrelated = (PostEffectFlags)1;
        effects.Flags = unrelated | PostEffectFlags.DepthOfField;
        interop.OnRender = () =>
        {
            if ((PostEffectManager.Current->Flags & PostEffectFlags.DepthOfField) == 0)
                throw new InvalidOperationException("Inactive suppression changed native blur");
        };
        interop.Render();
        suppression.Suppress = true;
        interop.OnRender = () =>
        {
            if (PostEffectManager.Current->Flags != unrelated)
                throw new InvalidOperationException("Native blur was not exclusively suppressed during render");
            PostEffectManager.Current->Flags |= (PostEffectFlags)2;
        };
        interop.Render();
        if (effects.Flags != (unrelated | (PostEffectFlags)2 | PostEffectFlags.DepthOfField))
            throw new InvalidOperationException("Original blur or unrelated render changes were not preserved");

        effects.Flags = unrelated;
        interop.OnRender = () => { };
        interop.Render();
        if (effects.Flags != unrelated)
            throw new InvalidOperationException("Originally disabled native blur became enabled");

        effects.Flags |= PostEffectFlags.DepthOfField;
        interop.OnRender = () => throw new IOException("Test render failure");
        try { interop.Render(); }
        catch (IOException) { }
        if ((effects.Flags & PostEffectFlags.DepthOfField) == 0)
            throw new InvalidOperationException("Render failure did not restore original blur");

        framework.IsInFrameworkUpdateThread = false;
        interop.OnRender = () =>
        {
            if ((PostEffectManager.Current->Flags & PostEffectFlags.DepthOfField) == 0)
                throw new InvalidOperationException("Off-thread render must be untouched");
        };
        interop.Render();
        framework.IsInFrameworkUpdateThread = true;
        PostEffectManager.Current = null;
        interop.OnRender = () => { };
        interop.Render();
        if (suppression.Error is null)
            throw new InvalidOperationException("Missing native resources must display an error");
        PostEffectManager.Current = &effects;
        interop.Render();
        if (suppression.Error is not null)
            throw new InvalidOperationException("Native resource recovery must clear the error");
        suppression.Suppress = false;
        interop.OnRender = () =>
        {
            if ((PostEffectManager.Current->Flags & PostEffectFlags.DepthOfField) == 0)
                throw new InvalidOperationException("Deactivation must restore normal native rendering");
        };
        interop.Render();
        suppression.Dispose();
        if (suppression.Suppress || !interop.Disposed)
            throw new InvalidOperationException("Unload must stop suppression and dispose the hook");
        PostEffectManager.Current = null;
    }

    private sealed class TestFramework : IFramework
    {
        public bool IsInFrameworkUpdateThread { get; set; } = true;
    }

    private sealed class TestInterop : IGameInteropProvider
    {
        private Manager.Delegates.Render? detour;
        public Action OnRender { get; set; } = () => { };
        public bool Disposed;
        public void Render() => detour!(null);
        public Hook<T> HookFromAddress<T>(nint address, T callback) where T : Delegate
        {
            detour = (Manager.Delegates.Render)(Delegate)callback;
            Manager.Delegates.Render original = _ => OnRender();
            return new Hook<T>((T)(Delegate)original, () => Disposed = true);
        }
    }
}

namespace Dalamud.Plugin.Services
{
    public interface IFramework { bool IsInFrameworkUpdateThread { get; } }
    public interface IGameInteropProvider
    {
        Hook<T> HookFromAddress<T>(nint address, T callback) where T : Delegate;
    }
}

namespace Dalamud.Hooking
{
    public sealed class Hook<T>(T original, Action dispose) : IDisposable where T : Delegate
    {
        public T Original => original;
        public void Enable() { }
        public void Dispose() => dispose();
    }
}

namespace FFXIVClientStructs.FFXIV.Client.Graphics.Render
{
    public unsafe struct Manager
    {
        public static class Delegates { public delegate void Render(Manager* manager); }
        public static class MemberFunctionPointers { public static nint Render => 1; }
    }
}

namespace FFXIVClientStructs.FFXIV.Client.Graphics.PostEffect
{
    [Flags]
    public enum PostEffectFlags : uint { DepthOfField = 1 << 7 }
    public unsafe struct PostEffectManager
    {
        public PostEffectFlags Flags;
        public static PostEffectManager* Current;
        public static PostEffectManager* Instance() => Current;
    }
}
