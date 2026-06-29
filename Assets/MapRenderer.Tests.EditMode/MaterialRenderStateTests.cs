// S58 acceptance — the typed render-state layer maps Unity rendering enums → the underlying ShaderLab
// int properties (_ZWrite/_ZTest/_Cull/_SrcBlend/_DstBlend/_BlendOp). Pure property round-trip on a
// Map/Fill material; no GUI, no scene.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests
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

        [Test]
        public void MapRenderState_ApplyTo_WritesAllFields()
        {
            var state = new MapRenderState
            {
                DepthWrite    = DepthWrite.Off,
                DepthTest     = CompareFunction.NotEqual,
                Cull          = CullMode.Off,
                SrcBlend      = BlendMode.SrcAlphaSaturate,
                DstBlend      = BlendMode.DstColor,
                SrcBlendAlpha = BlendMode.OneMinusDstAlpha,
                DstBlendAlpha = BlendMode.OneMinusSrcColor,
                BlendOp       = BlendOp.Add,
            };
            state.ApplyTo(_mat);

            Assert.AreEqual(0f, _mat.GetFloat(ShaderProperties.PropertyNames.ZWrite));
            Assert.AreEqual((int)CompareFunction.NotEqual, (int)_mat.GetFloat(ShaderProperties.PropertyNames.ZTest));
            Assert.AreEqual((int)CullMode.Off, (int)_mat.GetFloat(ShaderProperties.PropertyNames.CullMode));
            Assert.AreEqual((int)BlendMode.SrcAlphaSaturate, (int)_mat.GetFloat(ShaderProperties.PropertyNames.SrcBlend));
            Assert.AreEqual((int)BlendMode.DstColor, (int)_mat.GetFloat(ShaderProperties.PropertyNames.DstBlend));
            Assert.AreEqual((int)BlendMode.OneMinusDstAlpha, (int)_mat.GetFloat(ShaderProperties.PropertyNames.SrcBlendAlpha));
            Assert.AreEqual((int)BlendMode.OneMinusSrcColor, (int)_mat.GetFloat(ShaderProperties.PropertyNames.DstBlendAlpha));
            Assert.AreEqual((int)BlendOp.Add, (int)_mat.GetFloat(ShaderProperties.PropertyNames.BlendOp));
        }
    }
}