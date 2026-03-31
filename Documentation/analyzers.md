# Analyzers

Graphity uses pluggable language analyzers to extract nodes and edges from source code. Each analyzer implements the `ILanguageAnalyzer` interface and is registered with the ingestion pipeline.

## Analyzer Interface

```csharp
public interface ILanguageAnalyzer
{
    string Language { get; }
    IReadOnlySet<string> SupportedExtensions { get; }
    Task<AnalyzerResult> AnalyzeAsync(string filePath, string repoRoot, CancellationToken ct = default);
}

public interface ISolutionAnalyzer : ILanguageAnalyzer
{
    Task<AnalyzerResult> AnalyzeSolutionAsync(string solutionPath, CancellationToken ct = default);
}
```

Analyzers return an `AnalyzerResult` containing lists of `GraphNode` and `GraphRelationship` objects. The pipeline merges these into the knowledge graph.

---

## C# Analyzer (RoslynAnalyzer)

**File:** `src/Graphity.Core/Analyzers/CSharp/RoslynAnalyzer.cs`
**Parser:** Microsoft Roslyn (Microsoft.CodeAnalysis)
**Extensions:** `.cs`
**Interface:** `ISolutionAnalyzer` (solution-level analysis)

### How It Works

1. **Solution loading** — Uses `MSBuildWorkspace.Create()` to load the `.sln` file, which resolves project references, NuGet packages, and compilation settings.
2. **Syntax walking** — For each document in each project, creates a `CSharpSyntaxWalker` (inner `SymbolWalker` class) that visits declaration and invocation syntax nodes.
3. **Semantic resolution** — Uses `SemanticModel.GetDeclaredSymbol()` and `SemanticModel.GetSymbolInfo()` to resolve symbols with full type information, enabling precise call graph construction.

### What Gets Extracted

**Type declarations:**
- Namespaces (block-scoped and file-scoped)
- Classes, interfaces, structs, records, enums, delegates
- Nested types (inner classes, etc.)
- Partial classes (detected via `DeclaringSyntaxReferences.Length > 1`, marked with `isPartial` property)

**Members:**
- Methods (with visibility, modifiers, return type)
- Constructors
- Properties
- Fields
- Extension methods (marked with `isExtension` property)

**Relationships:**
- `Contains` — File contains type, namespace contains type
- `Defines` — Namespace defines type
- `HasMethod`, `HasProperty`, `HasField` — Type membership
- `Calls` — Method invocations (confidence 0.9 when Roslyn resolves the target, 0.5 otherwise)
- `Extends` — Base class inheritance
- `Implements` — Interface implementation
- `Overrides` — Virtual/abstract method overrides
- `Imports` — Using directives

### Fallback Behavior

If no `.sln` file is found in the repository root, the analyzer falls back to file-by-file analysis. This uses Roslyn's syntax-only mode without full semantic resolution, which means call confidence will be lower (0.5 instead of 0.9).

### Namespace Handling

The analyzer creates namespace nodes on-demand via `EnsureNamespaceNode()`. This prevents orphaned `Contains` edges when a namespace is referenced but hasn't been visited yet. Both block-scoped (`namespace Foo { }`) and file-scoped (`namespace Foo;`) namespaces are supported.

---

## C# Config Parser

**File:** `src/Graphity.Core/Analyzers/CSharp/CSharpConfigParser.cs`
**Extensions:** `.csproj`, `.json`, `.config`, `.props`, `.targets`

Parses configuration files alongside C# source code:

### .csproj Files

Extracts `PackageReference` elements to create:
- `NuGetPackage` nodes (with name and version)
- `ReferencesPackage` edges from the project file to each package

Deduplicates across projects — if two `.csproj` files reference the same NuGet package, only one node is created.

### appsettings.json

Creates:
- `ConfigFile` node for the file itself
- `ConfigSection` nodes for each top-level key
- `Contains` edges from file to sections

### web.config / app.config

Parses XML to create:
- `ConfigFile` node
- `ConfigSection` nodes for `appSettings`, `connectionStrings`, and `system.web` sections
- `Contains` edges

### Directory.Build.props

Creates a `ConfigFile` node to track the presence of centralized build configuration.

---

## TypeScript/JavaScript Analyzer

**File:** `src/Graphity.Core/Analyzers/TypeScript/TypeScriptAnalyzer.cs`
**Parser:** Regex-based extraction (no Tree-sitter dependency)
**Extensions:** `.ts`, `.tsx`, `.js`, `.jsx`

### Why Regex Instead of Tree-sitter

The plan originally called for Tree-sitter, but the .NET Tree-sitter bindings are unstable and introduce complex native dependencies. A regex-based approach was chosen for pragmatic reasons — it covers the most common patterns without native binary headaches.

### What Gets Extracted

**Imports** (ES6 and CommonJS):
```typescript
import { Foo, Bar } from './module'     // Named imports
import Default from './module'           // Default imports
import * as Mod from './module'          // Namespace imports
const x = require('./module')           // CommonJS require
```

**Type declarations:**
- Classes (with `extends` and `implements`)
- Interfaces
- Enums
- Functions (both declarations and arrow functions)

**Function extraction patterns:**
```typescript
function myFunc(params) { }              // Function declaration
const myFunc = (params) => { }           // Arrow function
const myFunc = function(params) { }      // Function expression
export const myFunc = ({x, y}) => { }    // Destructured params
```

**Call expressions:**
- Direct calls: `myFunction()`
- Member calls: `object.method()`
- Constructor calls: `new MyClass()`

**Built-in keyword filtering:**
The analyzer maintains a set of built-in keywords (`if`, `for`, `while`, `switch`, `return`, `new`, `delete`, `typeof`, `instanceof`, `void`, `throw`, `catch`, `finally`, `await`, `yield`, `class`, `const`, `let`, `var`, `import`, `export`, `from`, `as`, `default`, `true`, `false`, `null`, `undefined`, `this`, `super`, `console`, `window`, `document`, `global`, `process`, `require`, `module`, `exports`, `arguments`, `Array`, `Object`, `String`, `Number`, `Boolean`, `Date`, `Math`, `JSON`, `Promise`, `Error`, `RegExp`, `Map`, `Set`, `Symbol`, `Proxy`, `Reflect`, `WeakMap`, `WeakSet`, `parseInt`, `parseFloat`, `isNaN`, `isFinite`, `setTimeout`, `setInterval`, `clearTimeout`, `clearInterval`, `fetch`) that are excluded from call graph extraction to prevent false positives.

### Limitations

- No cross-file import resolution (imports create edges to the module path, not specific symbols)
- No type inference (arrow function return types not tracked)
- Deeply nested patterns may be missed by regex
- Dynamic calls (`obj[methodName]()`) not detected

---

## SQL Analyzer

**File:** `src/Graphity.Core/Analyzers/Sql/SqlAnalyzer.cs`, `SqlVisitor.cs`
**Parser:** Microsoft.SqlServer.TransactSql.ScriptDom (TSql160Parser)
**Extensions:** `.sql`

### How It Works

1. **Parsing** — `TSql160Parser` parses SQL files into an AST (`TSqlFragment` tree)
2. **Visiting** — `SqlVisitor` extends `TSqlFragmentVisitor` to walk the AST and extract schema information

### What Gets Extracted

**Tables** (from `CREATE TABLE`):
- `Table` node with columns
- `Column` nodes for each column definition
- `Contains` edges from table to columns

**Views** (from `CREATE VIEW`):
- `View` node
- `ReferencesTable` edges to tables referenced in the view's SELECT

**Stored Procedures and Functions:**
- `StoredProcedure` node
- `ReferencesTable` edges to tables referenced in the body
- Parameter metadata in properties

**Indexes** (from `CREATE INDEX`):
- `Index` node
- `ReferencesTable` edge to the indexed table

**Triggers** (from `CREATE TRIGGER`):
- `Trigger` node
- `ReferencesTable` edge to the trigger's target table

**Foreign Keys:**
- `ForeignKey` node
- `ForeignKeyTo` edge to the referenced table

### Line Number Accuracy

End-line numbers are calculated using `ScriptTokenStream[fragment.LastTokenIndex].Line` for accuracy, rather than estimating from fragment length.

---

## Adding a New Analyzer

To add support for a new language:

1. Create a new class implementing `ILanguageAnalyzer` (or `ISolutionAnalyzer` if it needs multi-file context)
2. Set `Language` and `SupportedExtensions`
3. Implement `AnalyzeAsync` to return `AnalyzerResult` with nodes and edges
4. Register it in `Program.cs`:

```csharp
pipeline.RegisterAnalyzer(new MyLanguageAnalyzer());
```

The `FileScanner` will automatically detect files with the registered extensions and pass them to your analyzer.
