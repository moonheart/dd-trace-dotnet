//------------------------------------------------------------------------------
// <auto-generated />
// This file was automatically generated by the UpdateVendors tool.
//------------------------------------------------------------------------------
#pragma warning disable CS0618, CS0649, CS1574, CS1580, CS1581, CS1584, SYSLIB0011,SYSLIB0032
// dnlib: See LICENSE.txt for more info

namespace Datadog.Trace.Vendors.dnlib.DotNet {
	/// <summary>
	/// Interface to access a local or a parameter
	/// </summary>
	internal interface IVariable {
		/// <summary>
		/// Gets the variable type
		/// </summary>
		TypeSig Type { get; }

		/// <summary>
		/// Gets the 0-based position
		/// </summary>
		int Index { get; }

		/// <summary>
		/// Gets/sets the variable name
		/// </summary>
		string Name { get; set; }
	}
}
