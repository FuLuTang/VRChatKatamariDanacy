using System;
using System.Reflection;
using NUnit.Framework;

namespace VRCKatamari.Tests
{
    /// <summary>
    /// EditMode tests for how the all-maps assembler judges and reports a value read from an
    /// Udon variable table: a missing symbol, a bool of the wrong value, and a value of the
    /// wrong type must each read as "not what was wanted" (the terrain-host bug was a table that
    /// did not hold <c>deferRegistration = true</c> although the editor-side proxy did).
    /// </summary>
    /// <remarks>
    /// Reflection, like the other editor-code tests: <c>UdonValueText</c> lives in
    /// <c>Assets/VRC_Katamari/Editor/Hub/</c>. The table access itself needs UdonSharp and a
    /// scene and is verified by the assembler's read-back, not here.
    /// </remarks>
    public class UdonValueTextTests
    {
        private const string TypeName = "VRCKatamari.Editor.Hub.UdonValueText";

        private Type type;

        [SetUp]
        public void SetUp()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length && type == null; i++)
            {
                type = assemblies[i].GetType(TypeName, false);
            }
            Assert.IsNotNull(type, TypeName + " not loaded");
        }

        [Test]
        public void ATrueBoolInTheTableIsWhatWasWanted()
        {
            Assert.IsTrue(IsBool(true, true, true));
        }

        [Test]
        public void AFalseBoolWhenTrueWasWantedIsAMismatch()
        {
            Assert.IsFalse(IsBool(true, false, true));
        }

        [Test]
        public void AMissingSymbolIsAMismatchWhateverTheValue()
        {
            Assert.IsFalse(IsBool(false, true, true));
            Assert.IsFalse(IsBool(false, null, true));
        }

        [Test]
        public void AValueOfAnotherTypeNeverMatches()
        {
            Assert.IsFalse(IsBool(true, 1, true));
            Assert.IsFalse(IsBool(true, "True", true));
            Assert.IsFalse(IsBool(true, null, true));
        }

        [Test]
        public void WantingFalseMatchesAFalseBool()
        {
            Assert.IsTrue(IsBool(true, false, false));
            Assert.IsFalse(IsBool(true, true, false));
        }

        [Test]
        public void DescribeNamesMissingNullAndTheValue()
        {
            Assert.AreEqual("missing", Describe(false, null));
            Assert.AreEqual("missing", Describe(false, true));
            Assert.AreEqual("null", Describe(true, null));
            Assert.AreEqual("True", Describe(true, true));
            Assert.AreEqual("False", Describe(true, false));
            Assert.AreEqual("12", Describe(true, 12));
        }

        private bool IsBool(bool found, object value, bool want)
        {
            return (bool)Call("IsBool", new object[] { found, value, want });
        }

        private string Describe(bool found, object value)
        {
            return (string)Call("Describe", new object[] { found, value });
        }

        private object Call(string name, object[] args)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(method, "UdonValueText." + name + " not found");
            return method.Invoke(null, args);
        }
    }
}
