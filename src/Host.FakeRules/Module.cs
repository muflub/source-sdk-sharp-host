using SourceSharp.Host.Contracts;
using SourceSharp.Host.Testing;

// The entry the host instantiates in the module's own load context (D-H9).
[assembly: HostRulesModule(typeof(FakeGameRules))]
