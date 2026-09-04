using Microsoft.ML.OnnxRuntime;

foreach (var path in args)
{
    Console.WriteLine($"=== {path} ===");
    using var session = new InferenceSession(path);
    Console.WriteLine("-- inputs --");
    foreach (var kv in session.InputMetadata)
    {
        Console.WriteLine($"  {kv.Key}: [{string.Join(",", kv.Value.Dimensions)}] {kv.Value.ElementDataType}");
    }
    Console.WriteLine("-- outputs --");
    foreach (var kv in session.OutputMetadata)
    {
        Console.WriteLine($"  {kv.Key}: [{string.Join(",", kv.Value.Dimensions)}] {kv.Value.ElementDataType}");
    }
}
