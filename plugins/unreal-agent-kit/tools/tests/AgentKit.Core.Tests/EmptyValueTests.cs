// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Tests;

/// <summary><see cref="UakArguments.GetStringOrEmpty"/>: an option whose empty value means "none".</summary>
[TestClass]
public sealed class EmptyValueTests
{
	[TestMethod]
	public void AnEmptyValueIsReadAsEmptyAndAnAbsentOneAsNull()
	{
		UakArguments Arguments = new(["-none=", "-some=-A -B=1", "-bare"], DotSplitRule.Options);

		Assert.AreEqual("", Arguments.GetStringOrEmpty("none"));
		Assert.AreEqual("-A -B=1", Arguments.GetStringOrEmpty("some"));
		Assert.IsNull(Arguments.GetStringOrEmpty("absent"));
		UakUsageException Error = Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetStringOrEmpty("bare"));
		Assert.Contains("-bare= for none", Error.Message);
		// Read, so it is no unknown option.
		Arguments.ThrowIfUnknown();
	}

	[TestMethod]
	public void GetStringStillRefusesAnEmptyValue()
	{
		UakArguments Arguments = new(["-none="]);

		Assert.ThrowsExactly<UakUsageException>(() => Arguments.GetString("none"));
	}
}
