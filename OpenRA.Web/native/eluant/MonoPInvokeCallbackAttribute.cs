// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Added to the upstream OpenRA/Eluant sources by OpenRA.Web/build-web.sh (not part of Eluant).
//
// Eluant already marks its four native-to-managed Lua callbacks with
// [MonoTouch.MonoPInvokeCallback] when built with MONOTOUCH defined. The .NET WebAssembly
// build needs exactly that marker: it can only call managed code from native code through
// thunks generated at build time for methods carrying an attribute named
// MonoPInvokeCallbackAttribute. Building Eluant with MONOTOUCH and this definition enables
// those attributes without changing any Eluant source file.
using System;

namespace MonoTouch
{
	[AttributeUsage(AttributeTargets.Method)]
	internal sealed class MonoPInvokeCallbackAttribute : Attribute
	{
		public MonoPInvokeCallbackAttribute(Type delegateType)
		{
			DelegateType = delegateType;
		}

		public Type DelegateType { get; private set; }
	}
}
