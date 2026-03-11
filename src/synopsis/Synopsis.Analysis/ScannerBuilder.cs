using Synopsis.Analysis.Roslyn;
using Synopsis.Analysis.Roslyn.Passes;

namespace Synopsis.Analysis;

public static class ScannerBuilder
{
    public static WorkspaceScanner Create() =>
        new(new WorkspaceLoader(),
            [new EndpointPass(), new HttpCallPass(), new DataAccessPass()]);
}
