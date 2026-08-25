using WinRT;

// Native AOT's CCW lookup-table generator needs an explicit assembly-level
// root for the local-server implementation type. This keeps the generated
// IFeedProvider vtable discoverable after trimming.
// Probe variant: rely only on [GeneratedWinRTExposedType] on the local type.
