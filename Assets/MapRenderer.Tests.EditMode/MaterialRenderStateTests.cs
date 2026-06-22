// S58 acceptance — the typed render-state layer maps Unity rendering enums → the underlying ShaderLab
// int properties (_ZWrite/_ZTest/_Cull/_SrcBlend/_DstBlend/_BlendOp). Pure property round-trip on a
// MapRenderer/Fill material; no GUI, no scene.
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MaterialRenderStateTests
    {
        private Material _mat;

        [SetUp]
        public void SetUp()
        {
            var shader = Shader.Find("MapRenderer/Fill");
            Assert.IsNotNull(shader, "MapRenderer/Fill shader must be present.");
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
            Assert.AreEqual(0f, _mat.GetFloat(ShaderProperties.ZWrite));
            _mat.SetDepthWrite(DepthWrite.On);
            Assert.AreEqual(1f, _mat.GetFloat(ShaderProperties.ZWrite));
            Assert.AreEqual(DepthWrite.On, _mat.GetDepthWrite());
        }

        [Test]
        public void SetDepthTest_WritesZTestInt_AndRoundTrips()
        {
            _mat.SetDepthTest(CompareFunction.LessEqual);
            Assert.AreEqual((int)CompareFunction.LessEqual, (int)_mat.GetFloat(ShaderProperties.ZTest));
            Assert.AreEqual(CompareFunction.LessEqual, _mat.GetDepthTest());
            _mat.SetDepthTest(CompareFunction.Always);
            Assert.AreEqual(CompareFunction.Always, _mat.GetDepthTest());
        }

        [Test]
        public void SetCull_WritesCullInt_AndRoundTrips()
        {
            _mat.SetCull(CullMode.Off);
            Assert.AreEqual((int)CullMode.Off, (int)_mat.GetFloat(ShaderProperties.CullMode));
            _mat.SetCull(CullMode.Back);
            Assert.AreEqual(CullMode.Back, _mat.GetCull());
        }

        [Test]
        public void SetBlend_WritesSrcDstInts()
        {
            _mat.SetBlend(BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
            Assert.AreEqual((int)BlendMode.SrcAlpha, (int)_mat.GetFloat(ShaderProperties.SrcBlend));
            Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)_mat.GetFloat(ShaderProperties.DstBlend));
        }

        [Test]
        public void MapRenderState_ApplyTo_WritesAllFields()
        {
            var state = new MapRenderState
            {
                DepthWrite = DepthWrite.Off,
                DepthTest  = CompareFunction.LessEqual,
                Cull       = CullMode.Off,
                SrcBlend   = BlendMode.SrcAlpha,
                DstBlend   = BlendMode.OneMinusSrcAlpha,
                BlendOp    = BlendOp.Add,
            };
            state.ApplyTo(_mat);

            Assert.AreEqual(0f, _mat.GetFloat(ShaderProperties.ZWrite));
            Assert.AreEqual((int)CompareFunction.LessEqual, (int)_mat.GetFloat(ShaderProperties.ZTest));
            Assert.AreEqual((int)CullMode.Off, (int)_mat.GetFloat(ShaderProperties.CullMode));
            Assert.AreEqual((int)BlendMode.SrcAlpha, (int)_mat.GetFloat(ShaderProperties.SrcBlend));
            Assert.AreEqual((int)BlendMode.OneMinusSrcAlpha, (int)_mat.GetFloat(ShaderProperties.DstBlend));
            Assert.AreEqual((int)BlendOp.Add, (int)_mat.GetFloat(ShaderProperties.BlendOp));
        }
    }
}
