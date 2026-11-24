using ShuangpinLink;

// Isolated interop fixture. Only synthetic files under the supplied directory are used.
var (mode, path) = (args[0], args[1]);
var source = File.ReadAllBytes(path);
var info = ImageFormats.Inspect(source);
if (mode == "strip")
{
    var output = ImageFormats.WithoutMetadata(source);
    if (ImageFormats.Inspect(output) != info) throw new InvalidDataException("Image dimensions changed.");
    File.WriteAllBytes(args[2], output);
    return;
}
var (role, id, time, folder) = (args[2], args[3], long.Parse(args[4]), args[5]);
string key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
var result = new byte[source.Length];
for (int i = 0; i < ImageWire.Count(source.Length); i++)
{
    string part = Path.Combine(folder, i + ".txt");
    if (mode == "seal") File.WriteAllText(part, ImageWire.Seal(key, role, id, time, source.Length, i, source));
    else if (mode == "open") ImageWire.Open(key, role, id, time, source.Length, i, File.ReadAllText(part), result);
    else throw new ArgumentException("Unknown fixture command.");
}
if (mode == "open" && !source.AsSpan().SequenceEqual(result)) throw new InvalidDataException("Image changed in transit.");
