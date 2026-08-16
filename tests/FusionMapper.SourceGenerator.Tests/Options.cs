using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionMapper.SourceGenerator.Tests;

sealed class OptionsProvider : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new Options();

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
    {
        return GlobalOptions;
    }

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
    {
        return GlobalOptions;
    }

    private sealed class Options : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
        {
            if (key == "build_property.EnableFusionMapperInterceptor")
            {
                value = "true";
                return true;
            }
            else if (key == "build_property.TargetFramework")
            {
                value = "net10.0";
                return true;
            }
            else
            {
                value = null;
                return false;
            }

        }
    }
}