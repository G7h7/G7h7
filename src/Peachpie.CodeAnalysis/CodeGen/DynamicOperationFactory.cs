﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeGen;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Symbols;
using Pchp.CodeAnalysis.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace Pchp.CodeAnalysis.CodeGen
{
    internal class DynamicOperationFactory
    {
        public class CallSiteData
        {
            /// <summary>
            /// CallSite_T.Target method.
            /// </summary>
            public FieldSymbol Target => _target;
            SubstitutedFieldSymbol _target;

            /// <summary>
            /// CallSite_T field.
            /// </summary>
            public IPlace Place => new FieldPlace(null, _fld, _cg.Module);
            SynthesizedFieldSymbol _fld;

            /// <summary>
            /// Gets CallSite.Create method.
            /// </summary>
            public MethodSymbol CallSite_Create => _callsite_create;
            MethodSymbol _callsite_create;

            /// <summary>
            /// Gets emitted callsite arguments.
            /// </summary>
            public ImmutableArray<TypeSymbol> Arguments => _arguments.AsImmutable();
            readonly List<TypeSymbol> _arguments = new List<TypeSymbol>();

            public ImmutableArray<RefKind> ArgumentsRefKinds
            {
                get
                {
                    if (_argumentsByRef == null)
                    {
                        return default(ImmutableArray<RefKind>);
                    }
                    else
                    {
                        while (_arguments.Count > _argumentsByRef.Count) _argumentsByRef.Add(RefKind.None);
                        return _argumentsByRef.AsImmutable();
                    }
                }
            }
            List<RefKind> _argumentsByRef = null;

            public void Prepare(CodeGenerator cg)
            {
                Debug.Assert(cg != null);
                _factory = cg.Factory;  // update cg and factory instance
                _arguments.Clear();
                _argumentsByRef = null;
            }

            public void Construct(NamedTypeSymbol functype, Action<CodeGenerator> binder_builder)
            {
                //CompilerLogSource.Log.Count("CallSite");

                //
                var callsitetype = _factory.CallSite_T.Construct(functype);

                // TODO: check if it wasn't constructed already

                _target.SetContainingType((SubstitutedNamedTypeSymbol)callsitetype);
                _fld.SetFieldType(callsitetype);
                _callsite_create = (MethodSymbol)_factory.CallSite_T_Create.SymbolAsMember(callsitetype);

                // create callsite

                // static .cctor {

                var cctor = _factory.CctorBuilder;
                lock (cctor)
                {
                    // fld = CallSite<T>.Create( <BINDER> )
                    var fldPlace = this.Place;
                    fldPlace.EmitStorePrepare(cctor);

                    var cg = _factory._cg;
                    using (var cctor_cg = new CodeGenerator(cctor, cg.Module, cg.Diagnostics, cg.DeclaringCompilation.Options.OptimizationLevel, false, _factory._container, null, null, cg.Routine)
                    {
                        CallerType = cg.CallerType,
                    })
                    {
                        binder_builder(cctor_cg);
                        cctor.EmitCall(_factory._cg.Module, _factory._cg.Diagnostics, ILOpCode.Call, this.CallSite_Create);

                        fldPlace.EmitStore(cctor);
                    }
                }

                // }
            }

            public void EmitLoadTarget()
            {
                Debug.Assert(_arguments.Count == 0);

                var il = _cg.Builder;

                // Template: LOAD callsite.Target
                this.Place.EmitLoad(il);
                il.EmitOpCode(ILOpCode.Ldfld);
                il.EmitSymbolToken(_factory._cg.Module, _factory._cg.Diagnostics, _target, null);
            }

            public void EmitLoadCallsite()
            {
                Debug.Assert(_arguments.Count == 0);

                // Template: LOAD callsite
                Place.EmitLoad(_cg.Builder);
            }

            DynamicOperationFactory _factory;

            /// <summary><see cref="CodeGenerator"/> instance.</summary>
            CodeGenerator _cg => _factory._cg;

            internal CallSiteData(DynamicOperationFactory factory, string fldname = null)
            {
                _factory = factory;

                _fld = factory.CreateCallSiteField(fldname ?? string.Empty);

                // AsMember // we'll change containing type later once we know, important to have Substitued symbol before calling it
                _target = new SubstitutedFieldSymbol(factory.CallSite_T, factory.CallSite_T_Target, _fld.MetadataName);
            }

            /// <summary>
            /// Notes arguments pushed on the stack to be passed to callsite.
            /// </summary>
            internal void AddArg(TypeSymbol t, bool byref)
            {
                if (byref)
                {
                    if (_argumentsByRef == null) _argumentsByRef = new List<RefKind>(_arguments.Count + 1);
                    while (_argumentsByRef.Count < _arguments.Count) _argumentsByRef.Add(RefKind.None);
                    _argumentsByRef.Add(RefKind.Ref);
                }

                _arguments.Add(t);
            }

            public TypeSymbol EmitTargetInstance(Func<CodeGenerator, TypeSymbol>/*!*/emitter)
            {
                Debug.Assert(emitter != null);
                var t = emitter(_cg);
                if (t != null)
                {
                    if (t.SpecialType == SpecialType.System_Void)
                    {
                        // void: invalid code, should be reported in DiagnosingVisitor
                        _cg.Builder.EmitNullConstant();
                        t = _cg.CoreTypes.Object;
                    }

                    AddArg(t, byref: false);
                }

                //
                return t;
            }

            /// <summary>Emits arguments to be passed to callsite.</summary>
            public void EmitArgs(ImmutableArray<BoundArgument> args)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    EmitArg(args[i]);
                }
            }

            /// <summary>Emits argument to be passed to callsite.</summary>
            public void EmitArg(BoundArgument a)
            {
                if (a.IsUnpacking)
                {
                    EmitUnpackingParam(_cg.Emit(a.Value));
                }
                else
                {
                    TypeSymbol t = null;
                    bool byref = false;

                    if (a.Value is BoundReferenceExpression varref && !varref.ConstantValue.HasValue)
                    {
                        // try read the value by ref,
                        // we might need the value ref in the callsite:

                        var bound = varref.BindPlace(_cg);
                        if (bound is BoundIndirectVariablePlace iplace)
                        {
                            t = iplace.LoadIndirectLocal(_cg); // IndirectLocal wrapper
                        }
                        else
                        {
                            var place = varref.Place(_cg.Builder);
                            if (place != null && place.HasAddress && place.TypeOpt == _cg.CoreTypes.PhpValue)
                            {
                                place.EmitLoadAddress(_cg.Builder);

                                t = place.TypeOpt;
                                byref = true;
                            }
                            else
                            {                                
                                bound.EmitLoadPrepare(_cg);
                                if ((bound.TypeOpt == null || bound.TypeOpt == _cg.CoreTypes.PhpValue) && // makes sense only if type is PhpValue (or unknown)
                                    (t = bound.EmitLoadAddress(_cg)) != null) // try to load address
                                {
                                    byref = true;
                                }
                                else
                                {
                                    t = bound.EmitLoad(_cg); // just load by value if address cannot be loaded
                                }
                            }
                        }
                    }

                    if (t == null)
                    {
                        t = _cg.Emit(a.Value);
                    }

                    if (t.SpecialType == SpecialType.System_Void)
                    {
                        Debug.Fail("Unexpected: argument evaluates to 'void'.");
                        // NOTE: this is an error somewhere, no expression shall return void
                        t = _cg.Emit_PhpValue_Null();
                    }

                    AddArg(t, byref: byref);
                }
            }

            /// <summary>Emits new instance of wrapper with value. Returns wrapper.</summary>
            public TypeSymbol EmitWrapParam(NamedTypeSymbol wrapper, ITypeSymbol value)
            {
                // Template: new wrapper(<STACK:value>)

                var ctor = wrapper.InstanceConstructors.Single(m => m.ParameterCount == 1);
                Debug.Assert((ITypeSymbol)ctor.Parameters[0].Type == value);
                var t = _cg.EmitCall(ILOpCode.Newobj, ctor);

                AddArg(t, byref: false);

                return t;
            }

            /// <summary>Template: &lt;ctx&gt;</summary>
            public void EmitLoadContext()
                => AddArg(_cg.EmitLoadContext(), byref: false);

            /// <summary>
            /// If needed in runtime, emits caller type context.
            /// Template: new CallerTypeParam(RuntimeTypeHandle)</summary>
            public TypeSymbol EmitCallerTypeParam()
            {
                var runtimectx = _cg.RuntimeCallerTypePlace;
                if (runtimectx != null)
                {
                    return EmitWrapParam(_cg.CoreTypes.Dynamic_CallerTypeParam, runtimectx.EmitLoad(_cg.Builder));
                }
                else
                {
                    return null;
                }
            }

            /// <summary>Template: new TargetTypeParam(PhpTypeInfo)</summary>
            public TypeSymbol EmitTargetTypeParam(IBoundTypeRef tref)
                => tref != null ? EmitWrapParam(_cg.CoreTypes.Dynamic_TargetTypeParam, tref.EmitLoadTypeInfo(_cg, true)) : null;

            /// <summary>Template: new NameParam{T}(STACK)</summary>
            public TypeSymbol EmitNameParam(BoundExpression expr)
            {
                if (expr != null)
                {
                    _cg.EmitConvert(expr, _cg.CoreTypes.String);
                    return EmitNameParam(_cg.CoreTypes.String);
                }
                else
                {
                    return null;
                }
            }

            /// <summary>Template: new NameParam{T}(STACK)</summary>
            public TypeSymbol EmitNameParam(TypeSymbol value)
                => EmitWrapParam(_cg.CoreTypes.Dynamic_NameParam_T.Symbol.Construct(value), value);

            /// <summary>Template: new UnpackingParam{T}(STACK)</summary>
            public TypeSymbol EmitUnpackingParam(TypeSymbol value)
                => EmitWrapParam(_cg.CoreTypes.Dynamic_UnpackingParam_T.Symbol.Construct(value), value);
        }

        readonly PhpCompilation _compilation;
        readonly NamedTypeSymbol _container;
        readonly CodeGenerator _cg;

        NamedTypeSymbol _callsitetype;
        NamedTypeSymbol _callsitetype_generic;
        MethodSymbol _callsite_generic_create;
        FieldSymbol _callsite_generic_target;

        public NamedTypeSymbol CallSite => _callsitetype ?? (_callsitetype = _compilation.GetWellKnownType(WellKnownType.System_Runtime_CompilerServices_CallSite));
        public NamedTypeSymbol CallSite_T => _callsitetype_generic ?? (_callsitetype_generic = _compilation.GetWellKnownType(WellKnownType.System_Runtime_CompilerServices_CallSite_T));
        public MethodSymbol CallSite_T_Create => _callsite_generic_create ?? (_callsite_generic_create = (MethodSymbol)_compilation.GetWellKnownTypeMember(WellKnownMember.System_Runtime_CompilerServices_CallSite_T__Create));
        public FieldSymbol CallSite_T_Target => _callsite_generic_target ?? (_callsite_generic_target = (FieldSymbol)_compilation.GetWellKnownTypeMember(WellKnownMember.System_Runtime_CompilerServices_CallSite_T__Target));

        public CallSiteData StartCallSite(string fldname) => new CallSiteData(this, fldname);

        /// <summary>
        /// Static constructor IL builder for dynamic sites in current context.
        /// </summary>
        public ILBuilder CctorBuilder => _cg.Module.GetStaticCtorBuilder(_container);

        public SynthesizedFieldSymbol CreateCallSiteField(string namehint) => _cg.Module.SynthesizedManager
            .GetOrCreateSynthesizedField(
                _container, CallSite, namehint, Accessibility.Private, true, true,
                autoincrement: true);

        /// <summary>
        /// Creates internal autoincremented field in current container.
        /// </summary>
        /// <param name="type">Field type.</param>
        /// <param name="name">Field name prefix.</param>
        /// <param name="isstatic">Whether the field is static.</param>
        /// <returns>The synthesized field.</returns>
        public SynthesizedFieldSymbol CreateSynthesizedField(TypeSymbol type, string name, bool isstatic) => _cg.Module.SynthesizedManager
            .GetOrCreateSynthesizedField(
                _container, type, name, Accessibility.Internal, isstatic, false, true);

        public DynamicOperationFactory(CodeGenerator cg, NamedTypeSymbol container)
        {
            Contract.ThrowIfNull(cg);
            Contract.ThrowIfNull(container);

            _cg = cg;
            _compilation = cg.DeclaringCompilation;
            _container = container;
        }

        internal NamedTypeSymbol GetCallSiteDelegateType(
            TypeSymbol loweredReceiver,
            RefKind receiverRefKind,
            ImmutableArray<TypeSymbol> loweredArguments,
            ImmutableArray<RefKind> refKinds,
            TypeSymbol loweredRight,
            TypeSymbol resultType)
        {
            Debug.Assert(refKinds.IsDefaultOrEmpty || refKinds.Length == loweredArguments.Length);

            var callSiteType = this.CallSite;
            if (callSiteType.IsErrorType())
            {
                return null;
            }

            var delegateSignature = MakeCallSiteDelegateSignature(callSiteType, loweredReceiver, loweredArguments, loweredRight, resultType);
            bool returnsVoid = resultType.SpecialType == SpecialType.System_Void;
            bool hasByRefs = receiverRefKind != RefKind.None || !refKinds.IsDefaultOrEmpty;

            if (!hasByRefs)
            {
                var wkDelegateType = returnsVoid ?
                    WellKnownTypes.GetWellKnownActionDelegate(invokeArgumentCount: delegateSignature.Length) :
                    WellKnownTypes.GetWellKnownFunctionDelegate(invokeArgumentCount: delegateSignature.Length - 1);

                if (wkDelegateType != WellKnownType.Unknown)
                {
                    var delegateType = _compilation.GetWellKnownType(wkDelegateType);
                    if (delegateType != null && !delegateType.IsErrorType())
                    {
                        return delegateType.Construct(delegateSignature);
                    }
                }
            }

            BitVector byRefs;
            if (hasByRefs)
            {
                byRefs = BitVector.Create(1 + (loweredReceiver != null ? 1 : 0) + loweredArguments.Length + (loweredRight != null ? 1 : 0));

                int j = 1;
                if (loweredReceiver != null)
                {
                    byRefs[j++] = receiverRefKind != RefKind.None;
                }

                if (!refKinds.IsDefault)
                {
                    for (int i = 0; i < refKinds.Length; i++, j++)
                    {
                        if (refKinds[i] != RefKind.None)
                        {
                            byRefs[j] = true;
                        }
                    }
                }
            }
            else
            {
                byRefs = default(BitVector);
            }

            int parameterCount = delegateSignature.Length - (returnsVoid ? 0 : 1);

            return _compilation.AnonymousTypeManager.SynthesizeDelegate(parameterCount, byRefs, returnsVoid).Construct(delegateSignature);
        }

        internal TypeSymbol[] MakeCallSiteDelegateSignature(TypeSymbol callSiteType, TypeSymbol receiver, ImmutableArray<TypeSymbol> arguments, TypeSymbol right, TypeSymbol resultType)
        {
            var systemObjectType = (TypeSymbol)_compilation.ObjectType;
            var result = new TypeSymbol[1 + (receiver != null ? 1 : 0) + arguments.Length + (right != null ? 1 : 0) + (resultType.SpecialType == SpecialType.System_Void ? 0 : 1)];
            int j = 0;

            // CallSite:
            result[j++] = callSiteType;

            // receiver:
            if (receiver != null)
            {
                result[j++] = receiver;
            }

            // argument types:
            for (int i = 0; i < arguments.Length; i++)
            {
                result[j++] = arguments[i];
            }

            // right hand side of an assignment:
            if (right != null)
            {
                result[j++] = right;
            }

            // return type:
            if (j < result.Length)
            {
                result[j++] = resultType;
            }

            return result;
        }

        internal SynthesizedStaticLocHolder DeclareStaticLocalHolder(string locName, TypeSymbol locType)
        {
            //// TODO: check the holder for static 'locName' isn't defined already
            //var holders = _cg.Module.SynthesizedManager.GetMembers<SynthesizedStaticLocHolder>(_container);
            //var holder = holders.FirstOrDefault(h => ReferenceEquals(h.DeclaringMethod, _cg.Routine) && h.VariableName == locName && h.ValueType == locType);
            //if (holder == null)
            //{
            var holder = new SynthesizedStaticLocHolder(_cg.Routine, locName, locType);
            _cg.Module.SynthesizedManager.AddNestedType(_container, holder);
            //}

            return holder;
        }
    }
}
