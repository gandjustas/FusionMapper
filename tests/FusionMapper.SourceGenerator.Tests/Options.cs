using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionMapper.SourceGenerator.Tests;

sealed class OptionsProvider(bool suppressUnmappedWarnings = false) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(suppressUnmappedWarnings);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
    {
        return GlobalOptions;
    }

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
    {
        return GlobalOptions;
    }

    private sealed class Options(bool suppressUnmappedWarnings) : AnalyzerConfigOptions
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
            else if (key == "build_property.FusionMapperSuppressUnmappedWarnings")
            {
                value = suppressUnmappedWarnings ? "true" : "false";
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