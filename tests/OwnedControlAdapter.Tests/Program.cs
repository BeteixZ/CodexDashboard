if (args.Length == 2 && args[0] == "--configuration")
    await CodexDashboard.OwnedControlConfigurationTests.RunAsync(Path.GetFullPath(args[1]));
else if (args.Length == 1)
    await CodexDashboard.OwnedControlAdapterTests.RunAsync(Path.GetFullPath(args[0]));
else throw new ArgumentException("Explicit new synthetic output directory required.");
