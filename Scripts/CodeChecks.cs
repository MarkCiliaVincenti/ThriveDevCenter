namespace Scripts;

using System.Collections.Generic;
using System.Text.RegularExpressions;
using ScriptsBase.Checks;

public class CodeChecks : CodeChecksBase<Program.CheckOptions>
{
    public CodeChecks(Program.CheckOptions opts) : base(opts)
    {
        FilePathsToAlwaysIgnore.Add(new Regex("Server/Migrations/"));
    }

    protected override Dictionary<string, CodeCheck> ValidChecks { get; } = new()
    {
        { "files", new FileChecks() },
        { "compile", new CompileCheck() },
        { "inspectcode", new InspectCode() },

        // Cleanup doesn't currently work nicely for the DevCenter (due to not working on .razor files)
        // { "cleanupcode", new CleanupCode() },
        { "rewrite", new RewriteTool() },
    };

    protected override IEnumerable<string> ForceIgnoredJetbrainsInspections =>
        new[]
        {
            "CSharpErrors",
            "Html.PathError",
            "DeclarationIsEmpty",
            "UnknownCssClass",

            // These refuse to read the right values even when configured by WebStorm.
            // Perhaps the issue is that Project_Default.xml is not read by the code inspection for some reason?
            "CssNotResolved",
            "CssBrowserCompatibility",
            "Es6Feature",

            // This is probably related to not detecting es6 or not detecting that things should be running in the
            // browser
            "UndeclaredGlobalVariableUsing",

            // This check seems to entirely work incorrectly for JavaScript
            "UnusedParameter",
        };

    protected override IEnumerable<string> ExtraIgnoredJetbrainsInspectWildcards => new[] { "Server/Migrations/*" };

    protected override string MainSolutionFile => "ThriveDevCenter.sln";
}
