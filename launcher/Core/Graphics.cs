namespace Playtest64.Launcher;

/// <summary>The graphics APIs the game can be started with. The words are the game's (P64_GRAPHICS, and --graphics= on its command line).</summary>
public static class Graphics
{
    public const string Auto = "auto";
    public const string Vulkan = "vulkan";
    public const string OpenGl = "opengl";

    /// <summary>In the order of the choice box.</summary>
    public static readonly string[] All = [Auto, Vulkan, OpenGl];

    public static readonly string[] Labels = ["Automatisch (empfohlen)", "Vulkan", "OpenGL (ältere Grafikkarten)"];

    public static bool IsKnown(string? value) => value != null && All.Contains(value);
}
