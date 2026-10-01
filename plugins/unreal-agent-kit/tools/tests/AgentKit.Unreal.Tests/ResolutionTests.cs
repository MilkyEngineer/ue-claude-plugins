// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using AgentKit.Core;

namespace AgentKit.Unreal.Tests;

[TestClass]
public sealed class ResolutionTests
{
	[TestMethod]
	public void TheEditorTargetIsTheOneWithEditorType()
	{
		using Sandbox Box = new();
		Assert.AreEqual("GameEditor", TargetResolver.ResolveEditorTarget(Box.ProjectFile));
	}

	[TestMethod]
	public void SeveralEditorTargetsPreferTheProjectNamedOne()
	{
		using Sandbox Box = new();
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "AnotherEditor.Target.cs"), "Type = TargetType.Editor;");
		Assert.AreEqual("GameEditor", TargetResolver.ResolveEditorTarget(Box.ProjectFile));

		File.Delete(Path.Combine(Box.ProjectDirectory, "Source", "GameEditor.Target.cs"));
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Source", "ThirdEditor.Target.cs"), "Type=TargetType.Editor;");
		UakSetupException Error = Assert.ThrowsExactly<UakSetupException>(() => TargetResolver.ResolveEditorTarget(Box.ProjectFile));
		Assert.Contains("AnotherEditor, ThirdEditor", Error.Message);
	}

	[TestMethod]
	public void AProjectWithoutSourceUsesTheEngineEditor()
	{
		using Sandbox Box = new();
		Directory.Delete(Path.Combine(Box.ProjectDirectory, "Source"), recursive: true);
		Assert.AreEqual("UnrealEditor", TargetResolver.ResolveEditorTarget(Box.ProjectFile));
	}

	[TestMethod]
	public void NoEditorTargetIsASetupError()
	{
		using Sandbox Box = new();
		File.Delete(Path.Combine(Box.ProjectDirectory, "Source", "GameEditor.Target.cs"));
		Assert.ThrowsExactly<UakSetupException>(() => TargetResolver.ResolveEditorTarget(Box.ProjectFile));
	}

	[TestMethod]
	public void FilesResolveByPathOrBareName()
	{
		using Sandbox Box = new();
		Assert.AreEqual(Box.SourceFile, SourceFiles.Resolve(Box.SourceFile, Box.Root, Box.ProjectDirectory));
		Assert.AreEqual(Box.SourceFile, SourceFiles.Resolve(Path.Combine("Game", "Source", "Game", "Private", "Foo.cpp"), Box.Root, Box.ProjectDirectory));
		Assert.AreEqual(Box.HeaderFile, SourceFiles.Resolve("foo.h", Box.Root, Box.ProjectDirectory));

		// Intermediate is never searched; a second real match is ambiguous.
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Intermediate", "Foo.cpp"), "");
		Assert.AreEqual(Box.SourceFile, SourceFiles.Resolve("Foo.cpp", Box.Root, Box.ProjectDirectory));
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "Source", "P", "Foo.cpp"), "");
		Assert.ThrowsExactly<UakUsageException>(() => SourceFiles.Resolve("Foo.cpp", Box.Root, Box.ProjectDirectory));
		Assert.ThrowsExactly<UakUsageException>(() => SourceFiles.Resolve("Missing.cpp", Box.Root, Box.ProjectDirectory));
		Assert.ThrowsExactly<UakUsageException>(() => SourceFiles.Resolve(Path.Combine("Nope", "Foo.cpp"), Box.Root, Box.ProjectDirectory));
	}

	[TestMethod]
	public void AFileKnowsItsModuleAndOwner()
	{
		using Sandbox Box = new();
		SourceFile Source = SourceFiles.Describe(Box.SourceFile);
		Assert.AreEqual("Game", Source.Module);
		Assert.AreEqual(Box.ProjectDirectory, Source.OwnerDirectory);
		Assert.IsFalse(Source.IsHeader);
		CollectionAssert.AreEqual(new[] { "Foo.cpp" }, Source.ActionItems.ToArray());

		SourceFile Header = SourceFiles.Describe(Box.HeaderFile);
		Assert.IsTrue(Header.IsHeader);
		CollectionAssert.AreEqual(new[] { "Foo.h.cpp", "Foo.h.obj" }, Header.ActionItems.ToArray());

		string PluginFile = Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "Source", "PRuntime", "Private", "Bar.cpp"), "");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "Source", "PRuntime", "PRuntime.Build.cs"), "");
		Sandbox.Write(Path.Combine(Box.ProjectDirectory, "Plugins", "P", "P.uplugin"), "{}");
		SourceFile Plugin = SourceFiles.Describe(PluginFile);
		Assert.AreEqual("PRuntime", Plugin.Module);
		Assert.AreEqual(Path.Combine(Box.ProjectDirectory, "Plugins", "P"), Plugin.OwnerDirectory);

		Assert.IsNull(SourceFiles.Describe(Box.ProjectFile).Module);
	}

	[TestMethod]
	public void OnlyTheFilesOwnSingleFileOutputsAreDeleted()
	{
		using Sandbox Box = new();
		string Build = Path.Combine(Box.ProjectDirectory, "Intermediate", "Build", "Win64", "x64", "UnrealEditor", "Development");
		string Mine = Sandbox.Write(Path.Combine(Build, "Game", "SingleFile", "Foo.cpp.obj"), "");
		string MyRsp = Sandbox.Write(Path.Combine(Build, "Game", "SingleFile", "Foo.cpp.rsp"), "");
		string OtherModule = Sandbox.Write(Path.Combine(Build, "Other", "SingleFile", "Foo.cpp.obj"), "");
		string RealObject = Sandbox.Write(Path.Combine(Build, "Game", "Foo.cpp.obj"), "");

		CollectionAssert.AreEqual(new[] { Mine }, SourceFiles.DeleteSingleFileOutputs(SourceFiles.Describe(Box.SourceFile), Box.ProjectDirectory));
		Assert.IsFalse(File.Exists(Mine));
		Assert.IsTrue(File.Exists(MyRsp) && File.Exists(OtherModule) && File.Exists(RealObject));

		// Nothing outside the writable root is touched.
		string Again = Sandbox.Write(Mine, "");
		Assert.IsEmpty(SourceFiles.DeleteSingleFileOutputs(SourceFiles.Describe(Box.SourceFile), Box.EngineRoot));
		Assert.IsTrue(File.Exists(Again));
	}
}
