using System;
using System.Reflection;
using System.Windows.Forms;

internal interface IMapViewport
{
    bool IsCurrent
    {
        get;
    }
    bool CanResize
    {
        get;
    }
    bool RendererSizeMatches
    {
        get;
    }
    bool Matches
    {
        get;
    }
    void Resize();
}

// Calls the game's ordinary upper-level resize routine on its owning UI thread.
// No graphics COM inspection, input rewriting, protected getter, or lower-level
// DXDevice resize call is used. Camera dimensions may settle on the next frame.
internal sealed class RendererViewport : IMapViewport
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Control Map;
    private readonly object Renderer, Args, Camera;
    private readonly FieldInfo Instance;
    private readonly MethodInfo OnResize;
    private readonly Func<bool> MapIdentity;

    private RendererViewport(Control map, object renderer, FieldInfo instance, MethodInfo resize, object args, object camera, Func<bool> mapIdentity)
    {
        Map = map;
        Renderer = renderer;
        Instance = instance;
        OnResize = resize;
        Args = args;
        Camera = camera;
        MapIdentity = mapIdentity;
    }

    internal static RendererViewport TryCreate(Control map, Func<bool> mapIdentity)
    {
        Type type = map.GetType().Assembly.GetType("DXRenderer.Main", true);
        FieldInfo instance = type.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object renderer = instance == null ? null : instance.GetValue(null);
        if (renderer == null)
            return null;
        MethodInfo resize = type.GetMethod("OnResize", Fields, null, new[] { typeof(Control) }, null);
        if (resize == null || resize.MetadataToken != 0x0600ac9a || resize.ReturnType != typeof(void))
            throw new InvalidOperationException("The reviewed renderer resize method is missing.");
        object args = Read(map, "L1PLqnGfHRf");
        if (args == null)
            return null;
        object camera = Read(args, "WorldCamera");
        if (camera == null)
            return null;
        RendererViewport result = new RendererViewport(map, renderer, instance, resize, args, camera, mapIdentity);
        if (!result.IsCurrent || !result.CanResize || !result.Matches)
            return null;
        int x = Int(renderer, "RGIyglBJZQK"), y = Int(renderer, "sO3yggQq7Xw");
        System.Drawing.Point screen = map.PointToScreen(System.Drawing.Point.Empty);
        bool originMatches = (x == map.ClientRectangle.Left && y == map.ClientRectangle.Top)
            || (x == screen.X && y == screen.Y);
        if (!originMatches || Int(renderer, "BeBygat7lGo") != 0 || Int(renderer, "Ne1ygi4S7C2") != 0)
            throw new InvalidOperationException("Renderer origin/margins do not match the reviewed map layout.");
        return result;
    }

    public bool IsCurrent
    {
        get
        {
            return !Map.IsDisposed && MapIdentity() && ReferenceEquals(Instance.GetValue(null), Renderer)
                && Args != null && Camera != null && ReferenceEquals(Read(Map, "L1PLqnGfHRf"), Args)
                && ReferenceEquals(Read(Renderer, "DrawArgs"), Args) && ReferenceEquals(Read(Args, "WorldCamera"), Camera);
        }
    }

    public bool CanResize
    {
        get
        {
            return IsCurrent && Equals(Read(Renderer, "Ready"), true) && Equals(Read(Renderer, "Initialized"), true)
                && !Equals(Read(Renderer, "Resizing"), true) && !Equals(Read(Renderer, "Drawing"), true);
        }
    }

    public bool RendererSizeMatches
    {
        get
        {
            return IsCurrent && Int(Renderer, "ScreenWidth") == Map.ClientSize.Width && Int(Renderer, "ScreenHeight") == Map.ClientSize.Height;
        }
    }

    public bool Matches
    {
        get
        {
            return RendererSizeMatches && Convert.ToDouble(Read(Camera, "ViewportWidth")) == Map.ClientSize.Width
                && Convert.ToDouble(Read(Camera, "ViewportHeight")) == Map.ClientSize.Height;
        }
    }

    public void Resize()
    {
        if (Map.InvokeRequired || !CanResize)
            throw new InvalidOperationException("Renderer resize requires the current idle map on its UI thread.");
        OnResize.Invoke(Renderer, new object[] { Map });
        if (!RendererSizeMatches)
            throw new InvalidOperationException("The renderer did not adopt the native map dimensions.");
    }

    private static int Int(object owner, string field)
    {
        return Convert.ToInt32(Read(owner, field));
    }

    private static object Read(object owner, string name)
    {
        if (owner == null)
            throw new InvalidOperationException("Missing owner for renderer field " + name);
        for (Type type = owner.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, Fields | BindingFlags.DeclaredOnly);
            if (field != null)
                return field.GetValue(owner);
        }
        throw new MissingFieldException(owner.GetType().FullName, name);
    }
}
