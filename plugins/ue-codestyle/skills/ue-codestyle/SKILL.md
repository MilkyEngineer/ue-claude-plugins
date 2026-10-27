---
name: ue-codestyle
description: An Unreal Engine C++ code style (MilkyEngineer's) - file and include layout, naming, class layout, reflection macros, comments, blank lines that give code breathing room, line breaking, lambdas, logging, column-aligned field blocks, and Build.cs files. Use it when writing, editing or reviewing Unreal Engine C++ (.h, .cpp, .inl) or .Build.cs files, unless the project has its own style guide or the file follows another style.
---

# Unreal Engine C++ code style

New code should read as if one person wrote the whole file. This guide is the house style. The [Epic C++ coding standard](https://dev.epicgames.com/documentation/en-us/unreal-engine/epic-cplusplus-coding-standard-for-unreal-engine) covers what it leaves out.

- **Precedence.** A project's own style guide, `.editorconfig` or `.clang-format` wins. In a file written in another style (engine code, a third-party library, code derived from Epic's), match that file instead.
- **Examples** use real tabs. Alignment assumes a tab width of 4.

## 1. Files

### Headers

```cpp
// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Interfaces/ISaveFeature.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "SKIMSaveSubsystem.generated.h"

class ISaveStateExtension;
enum class ESaveStateType : uint8;

DECLARE_DYNAMIC_DELEGATE_ThreeParams(FOnSaveComplete, const FString&, SaveName, int32, UserId, const FSKIMResult&, Result);
```

- The first line is the project's copyright line. Copy it from the project's other files.
- `#pragma once` comes next, then the includes, with the `.generated.h` include last. A blank line separates each part.
- Forward declarations and `using` declarations follow the includes, then a blank line.

### Source files

```cpp
// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

#include "State/WorldState.h"

#include "GameState.h"
#include "SKIMImpl_v1.h"
#include "Interfaces/ISaveFeature.h"

#include "SaveSystem/Helpers.h"
#include "State/LevelState.h"
#include "WorldPartition/WorldPartition.h"

using namespace UE::Tasks;

#define LOCTEXT_NAMESPACE UE_MODULE_NAME

// ...

#undef LOCTEXT_NAMESPACE
```

- The file's own header comes first, on its own. The other includes follow in groups, with a blank line between groups.
- A file compiled only with a feature wraps everything after its own header in the guard (`#if WITH_TEXT_ARCHIVE_SUPPORT` ... `#endif`).
- `#define LOCTEXT_NAMESPACE UE_MODULE_NAME` follows the includes, and `#undef LOCTEXT_NAMESPACE` ends the file.
- Explicit template instantiations go at the end of the file.

### Whitespace

- Indent with tabs, never spaces. Files end with a newline and have no trailing whitespace.
- Preprocessor directives stay at column 0, even inside a function or a class:

```cpp
TSharedPtr<ISaveData> ISaveData::CreateData()
{
#if WITH_TEXT_ARCHIVE_SUPPORT
	if (ISaveDataProvider::GetCount() == 1)
	{
		return ISaveDataProvider::Get(0).CreateData();
	}
#endif

	return MakeShared<FSaveData_Binary>();
}
```

- Long lines are fine: 150 to 200 characters is normal. Don't wrap a call or a signature only because it is long. Section 6 lists the cases that do break.

## 2. Naming

- **Prefixes and case.** Use the UE prefixes (`U`, `A`, `F`, `I`, `E`, `T`, `S`) and PascalCase for every name, including locals.
- **Bools** start with `b`: `bIsLoading`, `bHasScale`.
- **Parameters** that are stored in a member take `In` (`InWorld`, `InSaveName`). Parameters the function writes to take `Out` (`OutObjectDataArray`).
- **Globals** start with `G`. A console variable is a `G` global plus a `CVar_` reference named after its path:

```cpp
bool GSKIMEnable = true;
static FAutoConsoleVariableRef CVar_SKIM_Enable(
	TEXT("SKIM.Enable"),
	GSKIMEnable,
	TEXT("Enable or disable SKIM save game system"));
```

- **Related locals** share a prefix, joined by an underscore: `OptSlot_Location`, `OptSlot_Rotation`, `Record_Rotation`, `TaskEvent_Load`, `Lock_Versions`, `CallFunction_DelegatePin`.
- **Variants** of a base type are `<Base>_<Variant>`: `FSaveData_Binary`, `FSaveDataProvider_Json`, `FSaveStateExtension_WorldSubsystems`. A function that may run off the game thread can say so: `Serialize_Concurrent`.
- **Delegate parameters** are named in comments:

```cpp
DECLARE_DELEGATE_ThreeParams(FAsyncCallbackDelegate, const FString& /* SaveName */, const FPlatformUserId& /* UserId */, const FSKIMResult& /* Result */);
```

- **Pointers and references** bind to the type: `UObject* Outer`, `const FString& SaveName`.
- **`auto`** is only for iterators and lambdas. Spell out every other type.
- **Templates** are written `template<typename T>`, with no space before `<`.

## 3. Classes and structs

```cpp
/**
 * The subsystem that manages the lifetime of a save game.
 */
UCLASS()
class SKIM_API USKIMSaveSubsystem : public UGameInstanceSubsystem
{
	GENERATED_BODY()

public:
	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;

	UFUNCTION(BlueprintCallable, Category = "SKIM", meta = (AutoCreateRefTerm = "OnComplete"))
	void Load(const FOnSaveComplete& OnComplete);

	UFUNCTION(BlueprintCallable, Category = "SKIM")
	const FString& GetSaveName() const { return SaveName; }

	UPROPERTY(BlueprintAssignable, Category = "SKIM|Events")
	FOnSaveOperationEvent OnOperationStartedEvent;

private:
	FString         SaveName;
	FPlatformUserId SaveUserId;

	void OnOperationStarted(const ISaveFeature::FOperation& Operation);
	void OnOperationEnded(const ISaveFeature::FOperation& Operation);
};
```

- **Order.** `public:`, then `protected:`, then `private:`. In `private:`, fields come before helper functions. A blank line follows `GENERATED_BODY()`.
- **Groups.** Related declarations form a group with no blank lines inside it: the overrides of one interface, a getter and its setter, a family of virtuals. A blank line separates groups.
- **Reflected declarations.** A `UFUNCTION` or `UPROPERTY` sits on the line above its declaration, and a blank line follows each pair.
- **Specifiers** have a space on each side of `=`: `Category = "SKIM|Serialize"`, `meta = (DefaultToSelf = "Actor", Keywords = "Save")`. Categories nest with `|`.
- **Trivial bodies** stay on one line: `virtual FOperationEvent& OnOperationStarted() override { return OnOperationStartedEvent; }`.
- **Defaults.** A field with a fixed default takes a default member initializer: `bool bSuccess = true;`, `ISaveGameSystem* SaveGameSystem = nullptr;`. An empty destructor is `virtual ~FFoo() = default;`.
- **Interface regions.** An implementation marks each interface it implements with `#pragma region ISaveFeature` ... `#pragma endregion`, at column 0.
- **Initializer lists** break before the colon and before each comma, one tab in. An empty body is `{}` on its own line:

```cpp
FWorldState::FWorldState(const TSharedRef<FGameState>& InGameState, FName InPackageName)
	: GameState(*InGameState)
	, WorldExtensions(ISKIMImplModule::Get().InitializeExtensionsOfType(ESaveStateType::World))
	, WorldId(InPackageName)
{}
```

- **Local helper types.** A type that only one function uses is declared inside that function: `struct Local { static void OnLinkActivated(...); };`, or `struct FParms { ... };` for `ProcessEvent` parameters.

## 4. Column alignment

In a block of plain (non-reflected) fields, the names line up in one column and the trailing comments line up in another:

```cpp
struct FSkimSpawnRequest
{
	FGuid         PersistentId;
	const UClass* Class = nullptr;	// Resolved; an unloadable class becomes UnknownClass and no request
	UObject*      Outer = nullptr;	// Actors: the ULevel; Registered Objects: the resolved outer
	FName         Name;				// The recorded name, used only if free in Outer
	FTransform    Transform;		// Actors only
	AActor*       Owner = nullptr;	// Actors only, when live
};
```

- **The name column** uses spaces after the type, one space past the longest type, so it lines up at any tab width.
- **The comment column** uses tabs, one tab stop past the longest declaration.
- **All or nothing.** Every name in a block lines up, including the names with no comment. A blank line ends a block.
- **Enum values** with trailing comments align their comments the same way:

```cpp
UENUM(BlueprintType)
enum class ESaveExistsResult : uint8
{
	OK,				// Operation on the file completed successfully.
	DoesNotExist,	// Operation on the file failed, because the file was not found / does not exist.
	Corrupt,		// Operation on the file failed, because the file was corrupt.
	UnspecifiedError
};
```

- **Reflected fields are not aligned.** Each has its `UPROPERTY` on the line above and a blank line after it (section 3).

## 5. Blank lines: breathing room

A function reads as a series of steps. A blank line ends each step. A step is a guard, a setup cluster, a block, or a run of calls that do one thing.

```cpp
void FWorldState::SetWorld(UWorld* InWorld)
{
	if (World.IsValid() && InWorld == World.Get())
	{
		return;
	}

	UE_LOG(LogSKIM, Verbose, TEXT("%hs: %s %s"), __FUNCTION__, *WorldId.ToString(), *GetNameSafe(InWorld));

	if (InWorld == nullptr)
	{
		Cleanup();
		return;
	}

	World = InWorld;

	for (FSaveStateExtensionData& ExtensionData : WorldExtensions)
	{
		ExtensionData.Extension->OnPostWorldInitialization(InWorld);
	}

	OnActorSpawnedHandle = InWorld->AddOnActorSpawnedHandler(FOnActorSpawned::FDelegate::CreateSP(this, &FWorldState::OnActorSpawned));
	OnActorDestroyedHandle = InWorld->AddOnActorDestroyedHandler(FOnActorDestroyed::FDelegate::CreateSP(this, &FWorldState::OnActorDestroyed));
}
```

**Put a blank line:**
- after an early-return guard;
- after `Super::Foo(...);`;
- after the checks and the scope markers that open a function (`check(IsInGameThread());`, `QUICK_SCOPE_CYCLE_COUNTER(...)`), and after a scope lock;
- after a log line that opens a function, when the next line starts another step;
- after a cluster of declarations that set up the next step;
- after the closing brace of an `if`, `for`, `while`, `switch` or multi-line lambda, before the next statement;
- before a `return` that follows a block.

**Don't put a blank line:**
- between a declaration and the `if` that tests it, inside a loop or a short block (at the top of a function, a cluster of setup declarations still ends with a blank line);
- inside a run of calls that form one step, such as delegate bindings or field assignments;
- after an opening brace or before a closing brace;
- twice in a row.

```cpp
TArray<FSaveStateExtensionData> FSKIMImplModule::InitializeExtensionsOfType(ESaveStateType Type) const
{
	// TODO: Make this threadsafe
	const TArray<const ISaveStateExtension*>& TypeExtensions = RegisteredExtensions[static_cast<uint8>(Type)];
	TArray<FSaveStateExtensionData> InitializedExtensions;
	InitializedExtensions.Reserve(TypeExtensions.Num());

	for (const ISaveStateExtension* Extension : TypeExtensions)
	{
		InitializedExtensions.AddDefaulted_GetRef().Extension = Extension->Create();
	}

	return InitializedExtensions;
}
```

```cpp
	for (const TSharedRef<FLevelState>& LevelState : Levels)
	{
		for (const FPersistentId& PersistentId : LevelState->GetLoadedActors())
		{
			const TSharedRef<FObjectData>* ActorData = PersistentObjects.Find(PersistentId);
			if (ActorData && IsValid((*ActorData)->Object.Get()))
			{
				OutObjectDataArray.Add(*ActorData);
			}
		}
	}
```

A comment that introduces a step sits directly above it, after the blank line that ends the step before.

## 6. Line breaks

- **Braces** go on their own lines (Allman), for every block, even a one-line body.
- **Conditions.** A short condition stays on one line. When a condition has three or more terms, or long terms, break before each `&&` or `||`, one tab in. The same goes for a `return` or a `const bool` built from several terms:

```cpp
	if (GameState.IsValid()
		&& OldPersistentID.IsValid())
	{

	const bool bWrite = Actor->IsRootComponentMovable()
		&& (bHasLocation || bHasRotation || bHasScale);

	return Operation.IsValid()
		&& Operation->Type == ESaveOperationType::Load
		&& Operation->SaveName == SaveName;
```

- **Log, check and ensure macros** with several arguments past the format string put one argument per line, one tab in:

```cpp
	UE_LOG(LogSKIM, Error, TEXT("%hs: Mismatch of class type for %s from %s! Serializing to %s from %s"),
		__FUNCTION__,
		*GetPathNameSafe(Object),
		*PersistentId.ToString(),
		*GetPathNameSafe(Object->GetClass()),
		*GetPathNameSafe(OtherObjectData->Class.ResolveClass()));
```

- **Stream chains** put each `<<` on its own line, one tab in:

```cpp
	Record_Rotation
		<< SA_VALUE(GET_MEMBER_NAME_STRING_CHECKED(FRotator, Pitch), Rotation.Pitch)
		<< SA_VALUE(GET_MEMBER_NAME_STRING_CHECKED(FRotator, Yaw), Rotation.Yaw)
		<< SA_VALUE(GET_MEMBER_NAME_STRING_CHECKED(FRotator, Roll), Rotation.Roll);
```

- **`switch`.** Case labels sit at the `switch`'s indent, and their statements one tab in. Stacked labels share one body, and `default:` comes last:

```cpp
	switch (NewState)
	{
	case ELevelStreamingState::LoadedNotVisible:
		GameState->OnLevelLoaded(LevelStreaming, nullptr);
		break;
	case ELevelStreamingState::MakingVisible:
		GameState->OnLevelMakingVisible(LevelStreaming);
		break;
	default:
		break;
	}
```

- **Continuations** are always one tab in from the line they continue. Don't align them with spaces to an opening parenthesis.

## 7. Lambdas

- **As an argument,** a lambda's braces sit at the indent of the statement that holds it, and the call's remaining arguments follow the closing brace:

```cpp
	SavePipe.Launch(RegionName, [this, SaveName, UserId, OnLoadComplete]
	{
		// ...
	}, ETaskPriority::BackgroundNormal);
```

- **Parameter lists.** A lambda with no parameters has no `()`. A `mutable` one with no parameters is written `] () mutable`. A parameter list follows the `]` with no space: `[this](UWorldPartition* WorldPartition)`.
- **Captures** list what is used. Init captures convert as they capture: `[Actor = TWeakObjectPtr<AActor>(Actor)]`, `[this, Operation = CurrentOperation]`. A long capture list continues on the next line, one tab in.
- **Delegate factories** (`CreateWeakLambda`, `CreateSPLambda`, `CreateUObject`) that take a lambda put it on the next line, one tab in:

```cpp
	ISaveFeature::Get().Load(SaveName, SaveUserId, ISaveFeature::FAsyncCallbackDelegate::CreateWeakLambda(this,
		[this, OnComplete](const FString& InSaveName, const FPlatformUserId& UserId, const FSKIMResult& Result)
		{
			OnComplete.ExecuteIfBound(InSaveName, FPlatformMisc::GetUserIndexForPlatformUser(UserId), Result);
		}));
```

- **Stored lambdas** are named for what they do: `TFunction<void()> SetActorTransform = [...]`.

## 8. Checks and scopes

- **Thread assumptions are asserted** at the top of the function: `check(IsInGameThread());`.
- **Scoping.** A bare `{ }` block limits the scope of a lock or a named profiling event to the lines that need it.

## 9. Comments

- **Doc comments** in headers use Javadoc, with the engine's tags: `@param`, `@return`, `@note` for something a caller must know, and `@see` for related code. A one-line description can be `/** ... */` on one line:

```cpp
	/**
	 * Helper method to serialize an actor's transform if the actor is movable.
	 * If loading, will set the actor's transform.
	 *
	 * @param Archive The archive that the save game is serializing
	 * @param Actor The actor whose transform will be serialized
	 * @return true if the transform was serialized
	 */
```

- **Inline comments** are in sentence case, with no trailing period, and they explain why. A conversational "we" voice is fine:
  - `// We're already in the game thread, execute immediately`
  - `// We're not the matching world... bail`
  - `// Only serialize the live actors`
- **Placement.** A comment sits above the code it explains. A short note may trail a `check` or one term of a broken condition: `check(!bLoading || bPreSerialize); // Loading should *only* run during pre-serialize`. Field and enum comments trail in an aligned column (section 4). A comment about an `else if` branch sits on the line above the `else if`, right after the previous branch's closing brace.
- **Tags:**
  - `// TODO:` or `// TODO(name):` for work left to do;
  - `// Note:` for something a reader would otherwise miss;
  - `// HAX:` for a deliberate workaround.

## 10. Build.cs

```csharp
// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using UnrealBuildTool;

public class SKIM : ModuleRules
{
	public SKIM(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"Engine",
		});

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"CoreUObject",
			"Json",
		});

		if (Target.Type == TargetType.Editor)
		{
			PrivateDependencyModuleNames.AddRange(new string[]
			{
				"UnrealEd",
			});
		}
	}
}
```

- **Module lists** have one module per line, each with a trailing comma, and the braces on their own lines.
- **Each list is its own step,** with a blank line after it.

## 11. Before handing code back

- [ ] Tabs, Allman braces, and the file's copyright line, `#pragma once` and include order.
- [ ] Each step ends with a blank line. There are no blank lines at the edges of blocks and no double blank lines.
- [ ] Inside loops and short blocks, a declaration sits against the `if` that tests it.
- [ ] Every field block is aligned whole or not at all.
- [ ] Multi-term conditions and long log calls break as section 6 shows. Other long lines are left alone.
- [ ] Comments say why, in sentence case, above the code.
