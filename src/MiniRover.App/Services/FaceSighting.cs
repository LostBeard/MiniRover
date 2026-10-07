namespace MiniRover.App.Services;

/// <summary>A face found in a video frame: centre and size as fractions of the frame (0..1, x right, y down).</summary>
public readonly record struct FaceSighting(double X, double Y, double W, double H, float Confidence);
