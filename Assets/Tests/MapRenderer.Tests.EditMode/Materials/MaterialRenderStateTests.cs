// S58 acceptance — the typed render-state layer maps Unity rendering enums → the underlying ShaderLab
// int properties (_ZWrite/_ZTest/_Cull/_SrcBlend/_DstBlend/_BlendOp). Pure property round-trip on a
// Map/Fill material; no GUI, no scene.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Materials;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Materials
{
    [TestFixture]
    public class MaterialRenderStateTests
    {
        private Material _mat;

        [SetUp]
        public void SetUp()
        {
            var shader = Shader.Find("Map/Fill");
            Assert.IsNotNull(shader, "Map/Fill shader must be present.");
            _mat = new Material(shader);
        }

        [TearDown]
        public void TearDown()
        {
            if (_mat != null) Object.DestroyImmediate(_mat);
        }

        [Test]
        public void SetDepthWrite_WritesZWriteInt_AndRoundTrips()
        {
            _mat.SetDepthWrite(DepthWrite.Off);
            Assert.AreEqual(0f, _mat.GetFloat(ShaderProperties.PropertyNames.ZWrite));
            _mat.SetDepthWrite(DepthWrite.On);
            Assert.AreEqual(1f, _mat.GetFloat(ShaderProperties.PropertyNames.ZWrite));
            Assert.AreEqual(DepthWrite.On, _mat.GetDepthWrite());
        }

        [Test]
        public void SetDepthTest_WritesZTestInt_AndRoundTrips()
        {
            _mat.SetDepthTest(CompareFunction.LessEqual);
            Assert.AreEqual((int)CompareFunction.LessEqual, (int)_mat.GetFloat(ShaderProperties.PropertyNames.ZTest));
            Assert.AreEqual(CompareFunction.LessEqual, _mat.GetDepthTest());
            _mat.SetDepthTest(CompareFunction.Always);
            Assert.AreEqual(CompareFunction.Always, _mat.GetDepthTest());
        }

        [Test]
        public void SetCull_WritesCullInt_AndRoundTrips()
        {
            _mat.SetCull(CullMode.Off);
            Assert.AreEqual((int)CullMode.Off, (int)_mat.GetFloat(ShaderProperties.PropertyNames.CullMode));
            _mat.SetCull(CullMode.Back);
            Assert.AreEqual(CullMode.Back, _mat.GetCull());
        }

        [Test]
        public void SetBlend_WritesSrcDstInts()
        {
            _mat.SetBlend(BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
            Assert.AreEqual((int)BlendMode.SrcAlpha, (int)_mat.GetFloat(ShaderProperties.PropertyNames.SrcBlend));
            Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)_mat.GetFloat(ShaderProperties.PropertyNames.DstBlend));
        }

    }
}