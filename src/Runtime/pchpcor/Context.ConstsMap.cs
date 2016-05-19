﻿using Pchp.Core.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pchp.Core
{
    partial class Context
    {
        #region ConstName

        [DebuggerDisplay("{Name,nq}")]
        struct ConstName : IEquatable<ConstName>
        {
            public class ConstNameComparer : IEqualityComparer<ConstName>
            {

                public bool Equals(ConstName x, ConstName y) => x.Equals(y);

                public int GetHashCode(ConstName obj) => obj.GetHashCode();
            }

            /// <summary>
            /// Constant name.
            /// </summary>
            public readonly string Name;

            /// <summary>
            /// Whether the casing is ignored.
            /// <c>false</c> by default.
            /// </summary>
            public readonly bool CaseInsensitive;

            public ConstName(string name, bool caseInsensitive = false)
            {
                this.Name = name;
                this.CaseInsensitive = caseInsensitive;
            }

            public bool Equals(ConstName other)
            {
                return Name.Equals(other.Name,
                    (this.CaseInsensitive | other.CaseInsensitive)
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is ConstName && Equals((ConstName)obj);

            public override int GetHashCode() => Name.GetHashCode();
        }

        #endregion

        class ConstsMap : IEnumerable<KeyValuePair<string, PhpValue>>
        {
            /// <summary>
            /// Maps of constant name to its ID.
            /// </summary>
            readonly static Dictionary<ConstName, int> _map = new Dictionary<ConstName, int>(new ConstName.ConstNameComparer());

            /// <summary>
            /// Maps constant ID to its actual value, accross all contexts (application wide).
            /// </summary>
            static PhpValue[] _valuesApp = new PhpValue[32];

            /// <summary>
            /// Actual count of defined constant names.
            /// </summary>
            static int _countApp, _countCtx;

            /// <summary>
            /// Maps constant ID to its actual value in current context.
            /// </summary>
            PhpValue[] _valuesCtx = new PhpValue[_countCtx];

            static void EnsureArray(ref PhpValue[] arr, int size)
            {
                if (arr.Length < size)
                {
                    Array.Resize(ref arr, size * 2 + 1);
                }
            }

            /// <summary>
            /// Ensures unique constant ID for given constant name.
            /// Gets positive ID for runtime constant, negative ID for application constant.
            /// IDs are indexed from <c>1</c>. Zero is invalid ID.
            /// </summary>
            static int RegisterConstantId(string name, bool ignorecase = false, bool appConstant = false)
            {
                var cname = new ConstName(name, ignorecase);
                int idx;

                if (!_map.TryGetValue(cname, out idx))
                {
                    // TODO: W lock

                    // new constant ID, non zero
                    idx = appConstant
                        ? -(++_countApp)    // app constants are negative
                        : (++_countCtx);    //

                    _map.Add(cname, idx);
                }

                //
                return idx;
            }

            public static void DefineAppConstant(string name, PhpValue value, bool ignorecase = false)
            {
                // TODO: Assert value.IsScalar

                var idx = -RegisterConstantId(name, ignorecase, true);
                Debug.Assert(idx != 0);

                if (idx < 0)
                    throw new ArgumentException("runtime_constant_redefinition");   // runtime constant with this name was already defined

                EnsureArray(ref _valuesApp, idx);

                _valuesApp[idx - 1] = value;
            }

            public bool DefineConstant(string name, PhpValue value, bool ignorecase = false)
            {
                // TODO: Assert value.IsScalar

                var idx = RegisterConstantId(name, ignorecase, false);
                Debug.Assert(idx != 0);

                if (idx < 0)
                    throw new ArgumentException("app_constant_redefinition");   // app-wide constant with this name was already defined

                EnsureArray(ref _valuesCtx, idx);

                // TODO: check redefinition
                _valuesCtx[idx - 1] = value;

                //
                return true;
            }

            public PhpValue GetConstant(string name)
            {
                int idx;
                return _map.TryGetValue(new ConstName(name), out idx)
                    ? GetConstant(idx)
                    : PhpValue.Void;
            }

            PhpValue GetConstant(int idx)
            {
                Debug.Assert(idx != 0);

                PhpValue[] arr;
                if (idx < 0)
                {
                    idx = -idx;
                    arr = _valuesApp;
                }
                else
                {
                    arr = _valuesCtx;
                }

                if (idx <= arr.Length)
                {
                    return arr[idx - 1];
                }

                //
                return PhpValue.Void;
            }

            public bool IsDefined(string name) => GetConstant(name).IsSet;

            /// <summary>
            /// Enumerates all defined constants available in the context (including app-wide constants).
            /// </summary>
            public IEnumerator<KeyValuePair<string, PhpValue>> GetEnumerator()
            {
                // TODO: R lock
                foreach(var pair in _map)
                {
                    var value = GetConstant(pair.Value);
                    if (value.IsSet)
                    {
                        yield return new KeyValuePair<string, PhpValue>(pair.Key.Name, value);
                    }
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
