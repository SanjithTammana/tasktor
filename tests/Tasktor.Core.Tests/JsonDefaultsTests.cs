using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Tasktor.Core.Tests;

/// <summary>
/// The source generator passes init-only properties as constructor arguments, so a key missing from the JSON overwrites
/// a <c>= new()</c> default with null. A file written by hand, by a script or by an older build then crashes the
/// agent (it stopped the system monitor on Windows: consents.json without "plugins"). This test reads <c>{}</c> into every
/// object type of every JSON context and fails on any non-nullable reference property that comes back null.
/// </summary>
public sealed class JsonDefaultsTests
{
    public static IEnumerable<object[]> Contexts() =>
        new[] { typeof(Tasktor.Contracts.ContractsJsonContext).Assembly, typeof(Tasktor.Core.Storage.CoreJsonContext).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(JsonSerializerContext).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => new object[] { t.FullName! });

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Missing_keys_never_leave_a_non_nullable_property_null(string contextName)
    {
        var type = new[] { typeof(Tasktor.Contracts.ContractsJsonContext).Assembly, typeof(Tasktor.Core.Storage.CoreJsonContext).Assembly }
            .Select(a => a.GetType(contextName)).First(t => t is not null)!;
        var context = (JsonSerializerContext)type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var nullability = new NullabilityInfoContext();
        var problems = new List<string>();
        foreach (var info in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
                     .Select(p => (JsonTypeInfo)p.GetValue(context)!)
                     .Where(i => i.Kind == JsonTypeInfoKind.Object && !i.Type.IsAbstract && !i.Type.IsInterface))
        {
            object? value;
            try
            {
                value = JsonSerializer.Deserialize("{}", info);
            }
            catch (JsonException)
            {
                continue; // has required members: a missing key is rejected, not defaulted
            }
            catch (NotSupportedException)
            {
                continue; // polymorphic bases and the like
            }

            if (value is null)
            {
                continue;
            }

            foreach (var p in info.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetMethod is not null && !p.PropertyType.IsValueType && p.GetIndexParameters().Length == 0))
            {
                if (nullability.Create(p).ReadState == NullabilityState.NotNull && p.GetValue(value) is null)
                {
                    problems.Add(info.Type.Name + "." + p.Name);
                }
            }
        }

        Assert.True(problems.Count == 0, "null after reading {}: " + string.Join(", ", problems));
    }
}
