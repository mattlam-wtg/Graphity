# Graph Schema

This document defines all node types, edge types, and their properties in the Graphity knowledge graph.

## Node Types

Graphity defines 27 node types organized into 5 categories:

### Code Structure Nodes

| Type | Source | Description |
|------|--------|-------------|
| `File` | All | A source or config file in the repository |
| `Folder` | All | A directory in the repository |
| `Namespace` | C# | A namespace declaration (block-scoped or file-scoped) |

### Type Definition Nodes

| Type | Source | Description |
|------|--------|-------------|
| `Class` | C#, TS | Class definition |
| `Interface` | C#, TS | Interface definition |
| `Struct` | C# | Struct definition |
| `Record` | C# | Record type definition |
| `Enum` | C#, TS | Enumeration definition |
| `Delegate` | C# | Delegate type definition |

### Member Nodes

| Type | Source | Description |
|------|--------|-------------|
| `Method` | C#, TS | Method or member function |
| `Function` | TS | Standalone function (not a class member) |
| `Constructor` | C# | Constructor method |
| `Property` | C# | Property declaration |
| `Field` | C# | Field declaration |

### Database Nodes

| Type | Source | Description |
|------|--------|-------------|
| `Table` | SQL | Database table (from CREATE TABLE) |
| `View` | SQL | Database view (from CREATE VIEW) |
| `StoredProcedure` | SQL | Stored procedure or function |
| `Column` | SQL | Table or view column |
| `Index` | SQL | Database index |
| `Trigger` | SQL | Database trigger |
| `ForeignKey` | SQL | Foreign key constraint |

### Configuration Nodes

| Type | Source | Description |
|------|--------|-------------|
| `ConfigFile` | Config | Configuration file (appsettings.json, web.config) |
| `ConfigSection` | Config | A section or key within a config file |
| `NuGetPackage` | .csproj | A NuGet package reference |

### Computed Nodes

| Type | Source | Description |
|------|--------|-------------|
| `Community` | Detection | A functional cluster identified by Louvain algorithm |
| `Process` | Detection | An execution flow trace (entry point through call chain) |

## Edge Types

Graphity defines 16 edge types:

### Structural Edges

| Type | Description | Example |
|------|-------------|---------|
| `Contains` | Parent contains child | File → Class, Folder → File, Namespace → Class |
| `Defines` | Type defines a member | Class → Method, Namespace → Class |
| `HasMethod` | Type has a method member | Class → Method |
| `HasProperty` | Type has a property member | Class → Property |
| `HasField` | Type has a field member | Class → Field |

### Reference Edges

| Type | Description | Confidence? |
|------|-------------|-------------|
| `Imports` | Using/import statement | Yes |
| `Calls` | Method/function invocation | Yes (0.9 for resolved, 0.5 for unresolved) |
| `Extends` | Class inheritance | No |
| `Implements` | Interface implementation | No |
| `Overrides` | Method override | No |

### Database Edges

| Type | Description | Confidence? |
|------|-------------|-------------|
| `ReferencesTable` | Proc/view references a table | Yes |
| `ForeignKeyTo` | FK column references another table | No |

### Configuration Edges

| Type | Description |
|------|-------------|
| `ReferencesPackage` | Project references a NuGet package |
| `ConfiguredBy` | Code element configured by a config section |

### Organization Edges

| Type | Description | Properties |
|------|-------------|------------|
| `MemberOf` | Symbol belongs to a community | — |
| `StepInProcess` | Symbol is a step in an execution flow | `Step` (1-indexed position) |

## Node Properties

All nodes share these common properties:

```
Id:         string    Required. Unique identifier (see ID conventions below)
Name:       string    Required. Short display name
Type:       NodeType  Required. One of the 27 types above
FullName:   string?   Fully qualified name (e.g., "MyApp.Services.UserService")
FilePath:   string?   Relative path from repo root
StartLine:  int?      First line of the definition
EndLine:    int?      Last line of the definition
IsExported: bool      True if publicly visible
Language:   string    "csharp", "typescript", "javascript", or "sql"
Content:    string?   Source code snippet (when available)
Properties: Dict      Additional key-value metadata
```

### Additional Properties by Type

**Partial classes (C#):**
- `Properties["isPartial"] = true` — when a class spans multiple files

**Extension methods (C#):**
- `Properties["isExtension"] = true` — when the method is an extension method

**Community nodes:**
- `Properties["cohesion"]` — fraction of internal edges (0.0–1.0)
- `Properties["symbolCount"]` — number of member symbols

**Process nodes:**
- `Properties["stepCount"]` — number of steps in the execution flow
- `Properties["entryPointId"]` — ID of the entry point symbol
- `Properties["terminalId"]` — ID of the terminal symbol

## Edge Properties

```
Id:         string    Required. Unique identifier
SourceId:   string    Required. ID of the source node
TargetId:   string    Required. ID of the target node
Type:       EdgeType  Required. One of the 16 types above
Confidence: double    0.0–1.0 probability (default 1.0)
Reason:     string?   Explanation (e.g., "Roslyn:SemanticModel")
Step:       int?      Position in a process (for StepInProcess edges, 1-indexed)
Properties: Dict      Additional key-value metadata
```

## Node ID Conventions

Node IDs are structured strings that encode the type and fully qualified name:

```
File:src/Services/UserService.cs
Folder:src/Services
Namespace:MyApp.Services
Class:MyApp.Services.UserService
Interface:MyApp.Services.IUserService
Method:MyApp.Services.UserService.GetUser
Constructor:MyApp.Services.UserService..ctor
Property:MyApp.Services.UserService.Id
Field:MyApp.Services.UserService._logger
Table:dbo.Users
View:dbo.ActiveUsers
StoredProcedure:dbo.GetUserById
Column:dbo.Users.Id
ForeignKey:FK_Orders_Users
Index:IX_Users_Email
NuGetPackage:Newtonsoft.Json/13.0.3
ConfigFile:appsettings.json
ConfigSection:appsettings.json:ConnectionStrings
Community:community_0
Process:process_GetUser_SaveUser
```

The format is `{Type}:{QualifiedName}`, which makes IDs both human-readable and unique within the graph.

## Confidence Scoring

Edges with a `Confidence` value less than 1.0 indicate uncertainty in the relationship:

| Confidence | Meaning |
|------------|---------|
| 1.0 | Structural certainty (Contains, Defines, Extends, etc.) |
| 0.9 | Roslyn-resolved call (SemanticModel confirmed the target) |
| 0.5 | Unresolved call (name matched but symbol resolution failed) |
| 0.5+ | Community detection minimum threshold for edge inclusion |

The confidence threshold is used by community detection (minimum 0.5) and process tracing (minimum 0.5) to filter out low-quality edges.
