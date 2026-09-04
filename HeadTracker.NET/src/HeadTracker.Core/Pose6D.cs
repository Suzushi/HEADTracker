namespace HeadTracker.Core;

/// <summary>
/// A 6-DoF head pose in the tracker's internal convention (matches the legacy
/// Pose6DoF): yaw/pitch/roll in degrees, translation Tx/Ty/Tz.
/// </summary>
public readonly record struct Pose6D(
    double Yaw, double Pitch, double Roll,
    double Tx, double Ty, double Tz)
{
    public static readonly Pose6D Zero = new(0, 0, 0, 0, 0, 0);
}
