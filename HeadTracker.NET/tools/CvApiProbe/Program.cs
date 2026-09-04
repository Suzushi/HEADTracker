using System.Reflection;
using OpenCvSharp;

var cv2 = typeof(Cv2);
foreach (var name in new[] { "SolvePnP", "SolvePnPRansac", "ProjectPoints" })
{
    Console.WriteLine($"== {name} ==");
    foreach (var m in cv2.GetMethods(BindingFlags.Public | BindingFlags.Static)
                 .Where(m => m.Name == name))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {(p.IsOut ? "out " : "")}{p.Name}"));
        Console.WriteLine($"  {m.ReturnType.Name} {name}({ps})");
    }
}

Console.WriteLine("== Mat ctors taking arrays ==");
foreach (var c in typeof(Mat).GetConstructors())
{
    var ps = c.GetParameters();
    if (ps.Any(p => p.ParameterType.IsArray || p.ParameterType.Name.StartsWith("IEnumerable")))
    {
        Console.WriteLine("  Mat(" + string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")");
    }
}

Console.WriteLine("== Mat To* / Get* helpers ==");
foreach (var m in typeof(Mat).GetMethods().Where(m =>
             (m.Name.StartsWith("ToArray") || m.Name.StartsWith("Get") || m.Name == "Reshape"
              || m.Name == "ToRects" || m.Name == "Clone") && m.IsPublic))
{
    var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({ps})");
}
