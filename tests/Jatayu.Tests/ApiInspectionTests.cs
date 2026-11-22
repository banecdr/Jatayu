using System;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace Jatayu.Tests;

public class ApiInspectionTests
{
    private readonly ITestOutputHelper _output;

    public ApiInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectStbAndKtxTypes()
    {
        _output.WriteLine("=== StbImage8 Properties and Fields ===");
        foreach (var p in typeof(Brutal.StbApi.StbImage8).GetProperties())
            _output.WriteLine($"  Prop: {p.PropertyType.Name} {p.Name}");
        foreach (var f in typeof(Brutal.StbApi.StbImage8).GetFields())
            _output.WriteLine($"  Field: {f.FieldType.Name} {f.Name}");

        _output.WriteLine("=== Stb Static Methods ===");
        var stbType = typeof(Brutal.StbApi.Stb);
        foreach (var m in stbType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name.Contains("Load") || m.Name.Contains("Image"))
                _output.WriteLine($"  {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
        }
    }

    [Fact]
    public void InspectCommandBufferCopy()
    {
        _output.WriteLine("=== Searching CommandBuffer types and methods ===");
        var assemblies = new[] { typeof(Brutal.VulkanApi.Vulkan).Assembly, typeof(Brutal.VulkanApi.Abstractions.DeviceEx).Assembly };
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetExportedTypes())
            {
                if (type.Name.Contains("CommandBuffer", StringComparison.OrdinalIgnoreCase))
                {
                    _output.WriteLine($"Type: {type.FullName}");
                    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (m.Name.Contains("Copy") || m.Name.Contains("Barrier") || m.Name.Contains("Rendering") || m.Name.Contains("Cmd"))
                            _output.WriteLine($"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                }
            }
        }
    }

    [Fact]
    public void InspectVulkanSamplerAndFeatures()
    {
        _output.WriteLine("=== Searching all methods for Sampler and Descriptor creation ===");
        var assemblies = new[] { typeof(Brutal.VulkanApi.Vulkan).Assembly, typeof(Brutal.VulkanApi.Abstractions.DeviceEx).Assembly };
        foreach (var asm in assemblies)
        {
            foreach (var type in asm.GetExportedTypes())
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (m.Name.Contains("CreateSampler") || m.Name.Contains("CreateDescriptor") || m.Name.Contains("AllocateDescriptor"))
                    {
                        _output.WriteLine($"{type.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                }
            }
        }
    }
}
