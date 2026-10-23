using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arawn.GameCreator2.Networking.Editor.CI
{
    /// <summary>
    /// Collapses every shader to a single variant while the Malbers smoke player is built.
    /// The smoke player runs with <c>-nographics</c>; shader fidelity is irrelevant, while the
    /// installed URP and Malbers Shader Graph variant spaces otherwise dominate build time.
    /// The stripper is inert unless the smoke build sets its environment flag.
    /// </summary>
    public sealed class NetworkSmokeShaderStripper : IPreprocessShaders
    {
        public int callbackOrder => int.MinValue;

        public void OnProcessShader(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> data)
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("GC2_NETWORK_SMOKE_STRIP_SHADERS"),
                    "1",
                    StringComparison.Ordinal))
            {
                return;
            }

            while (data.Count > 1)
            {
                data.RemoveAt(data.Count - 1);
            }
        }
    }
}
