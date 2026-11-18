using TouchpadAgent.Tests;

UnitTests.Register();
ServerTests.Register();
return await T.RunAll(args.Length > 0 ? args[0] : null);
