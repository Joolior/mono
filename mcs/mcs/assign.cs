//
// assign.cs: Assignments.
//
// Author:
//   Miguel de Icaza (miguel@ximian.com)
//   Martin Baulig (martin@gnome.org)
//
// (C) 2001, 2002 Ximian, Inc.
//
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Mono.CSharp {

	/// <summary>
	///   This interface is implemented by expressions that can be assigned to.
	/// </summary>
	/// <remarks>
	///   This interface is implemented by Expressions whose values can not
	///   store the result on the top of the stack.
	///
	///   Expressions implementing this (Properties, Indexers and Arrays) would
	///   perform an assignment of the Expression "source" into its final
	///   location.
	///
	///   No values on the top of the stack are expected to be left by
	///   invoking this method.
	/// </remarks>
	public interface IAssignMethod {
		//
		// This method will emit the code for the actual assignment
		//
		void EmitAssign (EmitContext ec, Expression source);

		//
		// This method is invoked before any code generation takes
		// place, and it is a mechanism to inform that the expression
		// will be invoked more than once, and that the method should
		// use temporary values to avoid having side effects
		//
		// Example: a [ g () ] ++
		//
		void CacheTemporaries (EmitContext ec);
	}

	/// <summary>
	///   An Expression to hold a temporary value.
	/// </summary>
	/// <remarks>
	///   The LocalTemporary class is used to hold temporary values of a given
	///   type to "simulate" the expression semantics on property and indexer
	///   access whose return values are void.
	///
	///   The local temporary is used to alter the normal flow of code generation
	///   basically it creates a local variable, and its emit instruction generates
	///   code to access this value, return its address or save its value.
	/// </remarks>
	public class LocalTemporary : Expression, IMemoryLocation {
		LocalBuilder builder;
		
		public LocalTemporary (EmitContext ec, Type t)
		{
			type = t;
			eclass = ExprClass.Value;
			loc = Location.Null;
			builder = ec.GetTemporaryStorage (t);
		}

		public void Release (EmitContext ec)
		{
			ec.FreeTemporaryStorage (builder);
			builder = null;
		}
		
		public LocalTemporary (LocalBuilder b, Type t)
		{
			type = t;
			eclass = ExprClass.Value;
			loc = Location.Null;
			builder = b;
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			ec.ig.Emit (OpCodes.Ldloc, builder); 
		}

		public void Store (EmitContext ec)
		{
			ec.ig.Emit (OpCodes.Stloc, builder);
		}

		public void AddressOf (EmitContext ec, AddressOp mode)
		{
			ec.ig.Emit (OpCodes.Ldloca, builder);
		}
	}

	/// <summary>
	///   The Assign node takes care of assigning the value of source into
	///   the expression represented by target. 
	/// </summary>
	public class Assign : ExpressionStatement {
		protected Expression target, source, real_source;
		protected LocalTemporary temp = null, real_temp = null;
		protected Assign embedded = null;
		protected bool is_embedded = false;
		protected bool must_free_temp = false;

		public Assign (Expression target, Expression source, Location l)
		{
			this.target = target;
			this.source = this.real_source = source;
			this.loc = l;
		}

		protected Assign (Assign embedded, Location l)
			: this (embedded.target, embedded.source, l)
		{
			this.is_embedded = true;
		}

		public Expression Target {
			get {
				return target;
			}

			set {
				target = value;
			}
		}

		public Expression Source {
			get {
				return source;
			}

			set {
				source = value;
			}
		}

		public static void error70 (EventInfo ei, Location l)
		{
			Report.Error (70, l, "The event '" + ei.Name +
				      "' can only appear on the left-side of a += or -= (except when" +
				      " used from within the type '" + ei.DeclaringType + "')");
		}

		//
		// Will return either `this' or an instance of `New'.
		//
		public override Expression DoResolve (EmitContext ec)
		{
			// Create an embedded assignment if our source is an assignment.
			if (source is Assign)
				source = embedded = new Assign ((Assign) source, loc);

			real_source = source = source.Resolve (ec);
			if (source == null)
				return null;

			//
			// This is used in an embedded assignment.
			// As an example, consider the statement "A = X = Y = Z".
			//
			if (is_embedded && !(source is Constant)) {
				// If this is the innermost assignment (the "Y = Z" in our example),
				// create a new temporary local, otherwise inherit that variable
				// from our child (the "X = (Y = Z)" inherits the local from the
				// "Y = Z" assignment).
				if (embedded == null)
					real_temp = temp = new LocalTemporary (ec, source.Type);
				else
					temp = embedded.temp;

				// Set the source to the new temporary variable.
				// This means that the following target.ResolveLValue () will tell
				// the target to read it's source value from that variable.
				source = temp;
			}

			// If we have an embedded assignment, use the embedded assignment's temporary
			// local variable as source.
			if (embedded != null)
				source = (embedded.temp != null) ? embedded.temp : embedded.source;

			target = target.ResolveLValue (ec, source);

			if (target == null)
				return null;

			Type target_type = target.Type;
			Type source_type = real_source.Type;

			// If we're an embedded assignment, our parent will reuse our source as its
			// source, it won't read from our target.
			if (is_embedded)
				type = source_type;
			else
				type = target_type;
			eclass = ExprClass.Value;

			//
			// If we are doing a property assignment, then
			// set the `value' field on the property, and Resolve
			// it.
			//
			if (target is PropertyExpr){
				PropertyExpr property_assign = (PropertyExpr) target;

				if (source_type != target_type){
					source = ConvertImplicitRequired (ec, source, target_type, loc);
					if (source == null)
						return null;
				}

				//
				// FIXME: Maybe handle this in the LValueResolve
				//
				if (!property_assign.VerifyAssignable ())
					return null;

				return this;
			}

			if (target is IndexerAccess) {
				return this;
			}

			if (target is EventExpr) {
				EventInfo ei = ((EventExpr) target).EventInfo;

				Expression ml = MemberLookup (
					ec, ec.ContainerType, ei.Name,
					MemberTypes.Event, AllBindingFlags | BindingFlags.DeclaredOnly, loc);

				if (ml == null) {
				        //
					// If this is the case, then the Event does not belong 
					// to this Type and so, according to the spec
					// is allowed to only appear on the left hand of
					// the += and -= operators
					//
					// Note that target will not appear as an EventExpr
					// in the case it is being referenced within the same type container;
					// it will appear as a FieldExpr in that case.
					//
					
					if (!(source is Binary)) {
						error70 (ei, loc);
						return null;
					} else {
						Binary tmp = ((Binary) source);
						if (tmp.Oper != Binary.Operator.Addition &&
						    tmp.Oper != Binary.Operator.Subtraction) {
							error70 (ei, loc);
							return null;
						}
					}
				}
			}
			
			if (source is New && target_type.IsValueType){
				New n = (New) source;

				n.ValueTypeVariable = target;
				return n;
			}

			if (target.eclass != ExprClass.Variable && target.eclass != ExprClass.EventAccess){
				Report.Error (131, loc,
					      "Left hand of an assignment must be a variable, " +
					      "a property or an indexer");
				return null;
			}

			if ((source.eclass == ExprClass.Type) && (source is TypeExpr)) {
				source.Error118 ("variable or value");
				return null;
			} else if (source is MethodGroupExpr){
				((MethodGroupExpr) source).ReportUsageError ();
				return null;
			}

			if (target_type == source_type)
				return this;
			
			//
			// If this assignemnt/operator was part of a compound binary
			// operator, then we allow an explicit conversion, as detailed
			// in the spec. 
			//

			if (this is CompoundAssign){
				CompoundAssign a = (CompoundAssign) this;
				
				Binary b = source as Binary;
				if (b != null && b.IsBuiltinOperator){
					//
					// 1. if the source is explicitly convertible to the
					//    target_type
					//
					
					source = ConvertExplicit (ec, source, target_type, loc);
					if (source == null){
						Error_CannotConvertImplicit (loc, source_type, target_type);
						return null;
					}
				
					//
					// 2. and the original right side is implicitly convertible to
					// the type of target_type.
					//
					if (StandardConversionExists (a.original_source, target_type))
						return this;

					Error_CannotConvertImplicit (loc, a.original_source.Type, target_type);
					return null;
				}
			}
			
			source = ConvertImplicitRequired (ec, source, target_type, loc);
			if (source == null)
				return null;

			// If we're an embedded assignment, we need to create a new temporary variable
			// for the converted value.  Our parent will use this new variable as its source.
			// The same applies when we have an embedded assignment - in this case, we need
			// to convert our embedded assignment's temporary local variable to the correct
			// type and store it in a new temporary local.
			if (is_embedded || embedded != null) {
				type = target_type;
				temp = new LocalTemporary (ec, type);
				must_free_temp = true;
			}
			
			return this;
		}

		Expression EmitEmbedded (EmitContext ec)
		{
			// Emit an embedded assignment.
			
			if (real_temp != null) {
				// If we're the innermost assignment, `real_source' is the right-hand
				// expression which gets assigned to all the variables left of it.
				// Emit this expression and store its result in real_temp.
				real_source.Emit (ec);
				real_temp.Store (ec);
			}

			if (embedded != null)
				embedded.EmitEmbedded (ec);

			// This happens when we've done a type conversion, in this case source will be
			// the expression which does the type conversion from real_temp.
			// So emit it and store the result in temp; this is the var which will be read
			// by our parent.
			if (temp != real_temp) {
				source.Emit (ec);
				temp.Store (ec);
			}

			Expression temp_source = (temp != null) ? temp : source;
			((IAssignMethod) target).EmitAssign (ec, temp_source);
			return temp_source;
		}

		void ReleaseEmbedded (EmitContext ec)
		{
			if (embedded != null)
				embedded.ReleaseEmbedded (ec);

			if (real_temp != null)
				real_temp.Release (ec);

			if (must_free_temp)
				temp.Release (ec);
		}

		void Emit (EmitContext ec, bool is_statement)
		{
			if (target is EventExpr) {
				((EventExpr) target).EmitAddOrRemove (ec, source);
				return;
			}

			//
			// FIXME! We need a way to "probe" if the process can
			// just use `dup' to propagate the result
			// 
			IAssignMethod am = (IAssignMethod) target;

			if (this is CompoundAssign){
				am.CacheTemporaries (ec);
			}

			Expression temp_source;
			if (embedded != null) {
				temp_source = embedded.EmitEmbedded (ec);

				if (temp != null) {
					source.Emit (ec);
					temp.Store (ec);
					temp_source = temp;
				}
			} else
				temp_source = source;

			if (is_statement)
				am.EmitAssign (ec, temp_source);
			else {
				LocalTemporary tempo;

				tempo = new LocalTemporary (ec, source.Type);

				temp_source.Emit (ec);
				tempo.Store (ec);
				am.EmitAssign (ec, tempo);
				tempo.Emit (ec);
				tempo.Release (ec);
			}

			if (embedded != null) {
				if (temp != null)
					temp.Release (ec);
				embedded.ReleaseEmbedded (ec);
			}
		}
		
		public override void Emit (EmitContext ec)
		{
			Emit (ec, false);
		}

		public override void EmitStatement (EmitContext ec)
		{
			Emit (ec, true);
		}
	}

	
	//
	// This class is used for compound assignments.  
	//
	class CompoundAssign : Assign {
		Binary.Operator op;
		public Expression original_source;
		
		public CompoundAssign (Binary.Operator op, Expression target, Expression source, Location l)
			: base (target, source, l)
		{
			original_source = source;
			this.op = op;
		}

		public Expression ResolveSource (EmitContext ec)
		{
			return original_source.Resolve (ec);
		}

		public override Expression DoResolve (EmitContext ec)
		{
			original_source = original_source.Resolve (ec);
			if (original_source == null)
				return null;

			target = target.Resolve (ec);
			if (target == null)
				return null;
			
			//
			// Only now we can decouple the original source/target
			// into a tree, to guarantee that we do not have side
			// effects.
			//
			source = new Binary (op, target, original_source, loc);
			return base.DoResolve (ec);
		}
	}
}




