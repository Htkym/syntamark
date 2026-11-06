using System.Collections;
using System.Reflection;

internal static class MappingFusionChecks
{
    internal static void Run(Assembly runtime, Assembly portable)
    {
        foreach (var assembly in new[] { runtime, portable })
        {
            var host = new Host(assembly);
            var source = host.Projection("ab<q>cdXY", new Segment(0, 9, 0, 9, "Linear"));
            var builder = host.Builder("Zab<q>cdXY", 1, 4, 9, (2, 3));
            var published = host.Publish(builder, source);
            Check(host.Segments(published) == "0:2/1:2/Linear;2:3/-/Unknown;5:4/6:4/Linear", "Opaque split and body shift");
            Check(host.Mapping(published, 0, 9) == "Partial/1:2;6:4", "Opaque gap remains separate raw fragments");
            Check(host.Mapping(published, 2, 0) == "Unknown/", "An opaque boundary is not an exact insertion point");
            Check(host.Counters(builder) == (9L, 3, true), "Publication charges decoded units and final segment count once");

            var scanStop = host.Builder("Zab<q>cdXY", 1, 100, 8, (2, 3));
            host.Resource("scan", () => host.Publish(scanStop, source));
            Check(host.Counters(scanStop) == (5L, 0, true), "Scan stop preserves prior segment accounting and Unknown state");
            var outputStop = host.Builder("Zab<q>cdXY", 1, 3, 9, (2, 3));
            host.Resource("output", () => host.Publish(outputStop, source));
            Check(host.Counters(outputStop) == (9L, 0, true), "Publication output stop follows all validation scans");
            var overflow = host.Builder("ZZaa", 2, 10, 1);
            var outOfRange = host.Projection("aa", new Segment(0, 1, 0, 1, "Linear"), new Segment(1, 1, int.MaxValue - 1, 1, "Linear"));
            host.Overflow(() => host.Publish(overflow, outOfRange));
            Check(host.Counters(overflow) == (0L, 0, false), "All checked shifts precede publication Scan accounting");

            var maskedAtomic = host.Builder("Z&a;b", 1, 10, 10, (1, 1));
            var atomic = host.Publish(maskedAtomic, host.Projection("&b",
                new Segment(0, 1, 0, 3, "Atomic"), new Segment(1, 1, 3, 1, "Linear")));
            Check(host.Segments(atomic) == "0:1/-/Unknown;1:1/4:1/Linear", "Masking part of an Atomic token masks the whole token");
            Check(host.Mapping(atomic, 0, 2) == "Partial/4:1", "An Atomic token cannot leak a shortened raw slice");

            var normalized = host.Builder("Z\r\n\0", 1, 10, 10);
            var normalization = host.Publish(normalized, host.Projection("\n\uFFFD",
                new Segment(0, 1, 0, 2, "Atomic"), new Segment(1, 1, 2, 1, "Linear")));
            Check(host.Segments(normalization) == "0:1/1:2/Atomic;1:1/3:1/Atomic", "CRLF and NUL stay distinct Atomic tokens");
            Check(host.Mapping(normalization, 1, 0) == "Exact/3:0", "Adjacent Atomic token boundary remains exact");
            var entity = host.Builder("Z&#x1F600;", 1, 10, 10);
            var entityText = host.Publish(entity, host.Projection("😀", new Segment(0, 2, 0, 9, "Atomic")));
            Check(host.Mapping(entityText, 1, 0) == "Unknown/", "UTF-16 interior of an Atomic entity stays unknown");
            Check(host.Mapping(entityText, 0, 1) == "CoveringTokens/1:9", "Partial decoded entity query retains the complete raw token");

            var unresolved = host.Builder("Zab<q>cdXY", 1, 4, 100, (2, 3));
            host.Precharge(unresolved, 7, 1);
            var shifted = host.ShiftLink(unresolved, source, true);
            Check(host.Segments(host.Property(shifted, "Label")) == "0:2/1:2/Linear;2:3/-/Unknown;5:4/6:4/Linear", "Unresolved label uses the same mask");
            Check(host.Counters(unresolved) == (7L, 3, true), "Unresolved label charges only its added segments");
            Check(host.Range(host.Property(shifted, "TargetRawSpan")) == "1:0", "Known empty target is shifted, not discarded");
            var unresolvedStop = host.Builder("Zab<q>cdXY", 1, 3, 100, (2, 3));
            host.Precharge(unresolvedStop, 7, 1);
            host.Resource("output", () => host.ShiftLink(unresolvedStop, source, true));
            Check(host.Counters(unresolvedStop) == (7L, 1, false), "Unresolved label budget rejection precedes Unknown publication");

            var retained = host.Builder("Za\rb", 1, 2, 2);
            host.Precharge(retained, 2, 1);
            var definition = host.ShiftLink(retained, host.Projection("a\nb", new Segment(0, 3, 0, 3, "Linear")), false);
            Check(host.Segments(host.Property(definition, "Label")) == "0:3/1:3/Atomic", "Retained reference label normalizes raw CR precision");
            Check(host.Counters(retained) == (2L, 1, false), "Retained definition normalization does not rescan or emit");
        }
        Console.WriteLine("PASS mapping fusion: raw splits/Atomic boundaries/UTF16/normalization/exact Scan-Emit stops/precharged labels.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct Segment(int Start, int Length, int? RawStart, int RawLength, string Kind);

    private sealed class Host(Assembly assembly)
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private Type Core(string name) => assembly.GetType("Syntamark.Compilation." + name, true)!;
        private object New(Type type, params object?[] arguments) => Activator.CreateInstance(type, Members, null, arguments, null)!;
        private object Raw(int start, int length) => New(Core("MdRawRange"), start, length);
        internal object Property(object value, string name) => value.GetType().GetProperty(name, Members)!.GetValue(value)!;
        private object Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value)!;
        private void Set(object value, string name, object data) => value.GetType().GetField(name, Members)!.SetValue(value, data);

        internal object Projection(string text, params Segment[] segments)
        {
            var values = Array.CreateInstance(Core("MdSourceSegment"), segments.Length);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                values.SetValue(New(Core("MdSourceSegment"), Raw(segment.Start, segment.Length),
                    segment.RawStart.HasValue ? Raw(segment.RawStart.Value, segment.RawLength) : null,
                    Enum.Parse(Core("MdSegmentKind"), segment.Kind)), index);
            }
            return New(Core("MdTextProjection"), text, values);
        }

        internal object Builder(string raw, int bodyStart, int maxOutput, int maxScan, params (int Start, int Length)[] opaque)
        {
            var options = New(Core("MdOptions"), 1048576, maxOutput, 200, maxScan, "lithosharp-markdown/1", 1);
            var type = Core("MdParser").GetNestedType("FactsBuilder", BindingFlags.NonPublic)!;
            var builder = New(type, raw, "fusion", "fixture", null, options, CancellationToken.None, false);
            Set(builder, "body", Raw(bodyStart, raw.Length - bodyStart));
            var ranges = Array.CreateInstance(Core("MdRawRange"), opaque.Length);
            for (var index = 0; index < opaque.Length; index++)
                ranges.SetValue(Raw(opaque[index].Start, opaque[index].Length), index);
            Set(builder, "opaqueRanges", ranges);
            return builder;
        }

        internal object Publish(object builder, object projection) => builder.GetType().GetMethod("PublishText", Members)!
            .Invoke(builder, new object?[] { projection, true })!;

        internal object ShiftLink(object builder, object projection, bool mask)
        {
            var link = New(Core("MdLink"), Enum.Parse(Core("MdLinkKind"), "ReferenceDefinition"),
                Enum.Parse(Core("MdLinkResolution"), "UnresolvedReference"), false, Raw(0, 0), projection,
                string.Empty, null, Raw(0, 0), "label");
            return builder.GetType().GetMethod("ShiftLink", Members)!.Invoke(builder, new object?[] { link, mask })!;
        }

        internal void Precharge(object builder, long scan, int output)
        {
            var context = Field(builder, "context");
            Set(context, "scanUnits", scan);
            Set(context, "outputItems", output);
        }

        internal (long Scan, int Output, bool Unknown) Counters(object builder)
        {
            var context = Field(builder, "context");
            return ((long)Field(context, "scanUnits"), (int)Field(context, "outputItems"), (bool)Field(builder, "unknownMapping"));
        }

        internal string Range(object? value) => value is null ? "-" : Property(value, "Start") + ":" + Property(value, "Length");
        internal string Segments(object projection) => string.Join(";", ((IEnumerable)Property(projection, "SourceSegments")).Cast<object>()
            .Select(segment => Range(Property(segment, "DecodedSpan")) + "/" + Range(Property(segment, "RawSpan")) + "/" + Property(segment, "Kind")));

        internal string Mapping(object projection, int start, int length)
        {
            var result = Core("MdSourceMapping").GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new[] { projection, Raw(start, length) })!;
            return Property(result, "Precision") + "/" + string.Join(";", ((IEnumerable)Property(result, "RawFragments")).Cast<object>().Select(Range));
        }

        internal void Resource(string reason, Action action)
        {
            try
            {
                action();
                throw new InvalidOperationException("Expected resource stop: " + reason);
            }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().Name == "MdResourceLimit")
            {
                Check((string)Property(exception.InnerException!, "Reason") == reason, "Resource stop reason");
            }
        }

        internal void Overflow(Action action)
        {
            try
            {
                action();
                throw new InvalidOperationException("Expected checked shift overflow.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is OverflowException)
            {
            }
        }
    }
}
