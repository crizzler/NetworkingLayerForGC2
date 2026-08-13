using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkActionEndpointAuthoringTests
    {
        [Test]
        public void EndpointId_IsGeneratedSerializedAndStableAcrossOrdinaryValidation()
        {
            var gameObject = new GameObject("Endpoint Authoring Test");
            try
            {
                NetworkActionEndpoint endpoint =
                    gameObject.AddComponent<NetworkActionEndpoint>();
                InvokeOnValidate(endpoint);

                string generated = endpoint.EndpointId;
                Assert.That(generated, Has.Length.EqualTo(32));
                Assert.That(Guid.TryParseExact(generated, "N", out _), Is.True);
                Assert.That(endpoint.EndpointHash, Is.Not.Zero);

                gameObject.name = "Renamed Endpoint";
                InvokeOnValidate(endpoint);
                Assert.That(endpoint.EndpointId, Is.EqualTo(generated),
                    "Renaming or ordinary validation must not change the protocol address.");

                SerializedProperty id = new SerializedObject(endpoint)
                    .FindProperty("m_EndpointId");
                Assert.That(id.stringValue, Is.EqualTo(generated));
                Assert.That(id.tooltip, Does.Contain("Automatically generated"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Endpoint_UsesGuidedCustomInspector()
        {
            var gameObject = new GameObject("Endpoint Inspector Test");
            UnityEditor.Editor editor = null;
            try
            {
                NetworkActionEndpoint endpoint =
                    gameObject.AddComponent<NetworkActionEndpoint>();
                editor = UnityEditor.Editor.CreateEditor(endpoint);
                Assert.That(editor, Is.Not.Null);
                Assert.That(editor.GetType().FullName,
                    Is.EqualTo(
                        "Arawn.GameCreator2.Networking.Editor.NetworkActionEndpointEditor"));
            }
            finally
            {
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MainGuide_DocumentsEndpointIdentityFieldsBindingsAndTroubleshooting()
        {
            string path = Path.Combine(
                Application.dataPath,
                "docs/NETWORK-ACTIONS-AND-OBJECT-STATE.md");
            string documentation = File.ReadAllText(path);

            StringAssert.Contains("## Configure a Network Action Endpoint", documentation);
            StringAssert.Contains("### Endpoint ID", documentation);
            StringAssert.Contains("**Transport Network ID**", documentation);
            StringAssert.Contains("**Endpoint ID**", documentation);
            StringAssert.Contains("**Action ID**", documentation);
            StringAssert.Contains("**Authority Conditions**", documentation);
            StringAssert.Contains("**On Authority Committed**", documentation);
            StringAssert.Contains("**On Applied**", documentation);
            StringAssert.Contains("**On Snapshot Applied**", documentation);
            StringAssert.Contains("Regenerate Endpoint ID", documentation);
            StringAssert.Contains("Target Not Found", documentation);
        }

        private static void InvokeOnValidate(NetworkActionEndpoint endpoint)
        {
            MethodInfo method = typeof(NetworkActionEndpoint).GetMethod(
                "OnValidate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(endpoint, null);
        }
    }
}
