#if UNITY_6000_5_OR_NEWER
using GameCreator.Runtime.Common;
using UnityEngine;

// Keep this assembly warning-free before the project-level compatibility bootstrap has run.
// The bootstrap marks GC2's generic base itself so sibling transport assemblies are covered too.
[assembly: MakeSerializable(typeof(TPropertyTypeGet<double>))]
[assembly: MakeSerializable(typeof(TPropertyTypeGet<string>))]
#endif
