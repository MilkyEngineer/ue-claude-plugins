// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

// The uak host. Everything lives in AgentKit.Core's UakHost, so tests can run it in-process; commands come from every
// AgentKit.* assembly next to this one, the folders in UAK_COMMAND_PATHS, and <Project>/.uak/commands when UAK_PROJECT_COMMANDS=1.
using AgentKit.Core;

return await UakHost.RunAsync(args, new UakHostOptions { HandleCtrlC = true }).ConfigureAwait(false);
