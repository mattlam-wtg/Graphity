namespace Graphity.Core.Graph;

public enum NodeType
{
    File, Folder, Namespace, Class, Interface, Struct, Record, Enum, Delegate,
    Method, Function, Constructor, Property, Field,
    Table, View, StoredProcedure, Column, Index, Trigger, ForeignKey,
    ConfigFile, ConfigSection, NuGetPackage,
    Community, Process
}

public enum EdgeType
{
    Contains, Defines, Imports, Calls, Extends, Implements, Overrides,
    HasMethod, HasProperty, HasField,
    ReferencesTable, ReferencesPackage, ForeignKeyTo, ConfiguredBy,
    MemberOf, StepInProcess
}
