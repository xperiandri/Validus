﻿module Validus

open System

// ------------------------------------------------
// Validation Errors
// ------------------------------------------------

/// A mapping of fields and errors
type ValidationErrors = private { ValidationErrors : Map<string, string list> } with
    member x.Value = x.ValidationErrors

let inline private validationErrors x = { ValidationErrors = x }    

/// Functions for ValidationErrors type
module ValidationErrors =
    /// Empty ValidationErrors, alias for Map.empty<string, string list>
    let empty : ValidationErrors = Map.empty<string, string list> |> validationErrors

    /// Create a new ValidationErrors instance from a field  and errors list
    let create (field : string) (errors : string list) : ValidationErrors =   
        [ field, errors ] |> Map.ofList |> validationErrors

    /// Combine two ValidationErrors instances
    let merge (e1 : ValidationErrors) (e2 : ValidationErrors) : ValidationErrors = 
        Map.fold 
            (fun acc k v -> 
                match Map.tryFind k acc with
                | Some v' -> Map.add k (v' @ v) acc
                | None    -> Map.add k v acc)
            e1.Value
            e2.Value
        |> validationErrors

    /// Unwrap ValidationErrors into a standard Map<string, string list>
    let toMap (e : ValidationErrors) : Map<string, string list> =        
        e.Value

    /// Unwrap ValidationErrors and collection individual errors into
    /// string list, excluding keys
    let toList (e : ValidationErrors) : string list =
        e 
        |> toMap
        |> Seq.collect (fun kvp -> kvp.Value)
        |> List.ofSeq


// ------------------------------------------------
// Validation Results
// ------------------------------------------------

/// The ValidationResult type represents a choice between success and failure
type ValidationResult<'a> = Success of 'a | Failure of ValidationErrors

/// A validation message for a field
type ValidationMessage = string -> string

/// Given a value, return true/false to indicate validity
type ValidationRule<'a> = 'a -> bool

/// Given a field name and value, 'a, produces a ValidationResult<'a>
type Validator<'a> = string -> 'a -> ValidationResult<'a>

/// Functions for ValidationResult type
module ValidationResult = 
    /// Convert regular value 'a into ValidationResult<'a>
    let retn (v : 'a) = Success v

    /// Bind content of ValidationResult<'a> to 'a -> ValidationResult<'b>
    let bind (resultFn : 'a -> ValidationResult<'b>) (result : ValidationResult<'a>)  : ValidationResult<'b> =
        match result with 
        | Success x -> resultFn x
        | Failure e -> Failure e

    /// Unpack ValidationResult and feed into validation function
    let apply (resultFn : ValidationResult<'a -> 'b>) (result : ValidationResult<'a>) : ValidationResult<'b> =
        match resultFn, result with
        | Success fn, Success x  -> fn x |> Success
        | Failure e, Success _   -> Failure e
        | Success _, Failure e   -> Failure e
        | Failure e1, Failure e2 -> Failure (ValidationErrors.merge e1 e2)  

    /// Create a ValidationResult<'a> based on condition, yield
    /// error message if condition evaluates false
    let create (condition : bool) (value : 'a) (error : ValidationErrors) : ValidationResult<'a> =
        if condition then Success value
        else error |> Failure

    /// Unpack ValidationResult, evaluate function if Success or return if Failure
    let map (fn : 'a -> 'b) (result : ValidationResult<'a>) : ValidationResult<'b> =
        apply (retn fn) result

    /// Transform ValidationResult<'a> to Result<'a, ValidationErrors>
    let toResult (result : ValidationResult<'a>) : Result<'a, ValidationErrors> =
        match result with 
        | Success r -> Ok r
        | Failure e -> Error e

    /// Transform & flatten ValidationResult<'a> to Result<'a, string list>
    let flatten (x : ValidationResult<'a>) : Result<'a, string list> = 
        x 
        |> toResult
        |> Result.mapError ValidationErrors.toList

    /// Convert ValidationResult<'a> seq into ValidationResult<'a seq>
    let sequence (items : ValidationResult<'a> seq) : ValidationResult<'a seq> =
        items
        |> Seq.fold (fun acc i ->
            match (i, acc) with
            | Success i, Success acc -> Success (Seq.append acc (seq { i }))
            | _, Failure e
            | Failure e, _ -> Failure e) (Success Seq.empty)    

    /// Create a tuple form ValidationResult, if two ValidationResult objects are 
    /// in Success state, otherwise return failure
    let zip (r1 : ValidationResult<'a>) (r2 : ValidationResult<'b>) : ValidationResult<'a * 'b> =
        match r1, r2 with
        | Success x1res, Success x2res -> Success (x1res, x2res)
        | Failure e1, Failure e2       -> Failure (ValidationErrors.merge e1 e2)
        | Failure e, _                 -> Failure e
        | _, Failure e                 -> Failure e


// ------------------------------------------------
// Validators
// ------------------------------------------------

/// Validation rules
module ValidationRule =
    let equality<'a when 'a : equality> (equalTo : 'a) : ValidationRule<'a> = 
        fun v -> v = equalTo
    
    let inequality<'a when 'a : equality> (notEqualTo : 'a) : ValidationRule<'a>= 
        fun v -> not(v = notEqualTo)

    let between<'a when 'a : comparison> (min : 'a) (max : 'a) : ValidationRule<'a> = 
        fun v -> v >= min && v <= max            

    let greaterThan<'a when 'a : comparison> (min : 'a) : ValidationRule<'a> = 
        fun v -> v > min

    let lessThan<'a when 'a : comparison> (max : 'a) : ValidationRule<'a> = 
        fun v -> v < max

    let betweenLen (min : int) (max : int) : ValidationRule<string> =
        fun str -> str.Length |> between min max

    let equalsLen (len : int) : ValidationRule<string> =
        fun str -> str.Length |> equality len

    let greaterThanLen (min : int) : ValidationRule<string> =
        fun str -> str.Length |> greaterThan min

    let lessThanLen (max : int) : ValidationRule<string> =
        fun str -> str.Length |> lessThan max

    let pattern (pattern : string) : ValidationRule<string> =
        fun v -> Text.RegularExpressions.Regex.IsMatch(v, pattern)

/// Functions for Validator type
module Validator =     
    /// Combine two Validators
    let compose (v1 : Validator<'a>) (v2 : Validator<'a>) : Validator<'a> =
        fun (field : string) (value : 'a) ->            
            match v1 field value, v2 field value with
            | Success a, Success _   -> Success a
            | Failure e, Success _   -> Failure e
            | Success _, Failure e   -> Failure e
            | Failure e1, Failure e2 -> Failure (ValidationErrors.merge e1 e2)                           

    /// Create a new Validator
    let create (message : ValidationMessage) (rule : ValidationRule<'a>) : Validator<'a> = 
        fun (field : string) (value : 'a) ->
            let error = ValidationErrors.create field [ message field ]
            ValidationResult.create (rule value) value error

/// Validation functions for prim itive types
module Validators = 
    /// Execute validator if 'a is Some, otherwise return Success 'a
    let optional (validator : Validator<'a>) (field : string) (value : 'a option): ValidationResult<'a option> =  
        match value with
        | Some v -> validator field v |> ValidationResult.map (fun v -> Some v)
        | None   -> Success value

    /// Execute validator if 'a is Some, otherwise return Failure 
    let required (validator : Validator<'a>) (message : ValidationMessage) (field : string) (value : 'a option) : ValidationResult<'a> =          
        match value with
        | Some v -> validator field v
        | None   -> Failure (ValidationErrors.create field [ message field ])           
         
    type EqualityValidator<'a when 'a : equality>() =
        /// Value is equal to provided value
        member _.equals (equalTo : 'a) (message : ValidationMessage) : Validator<'a> =            
            let rule = ValidationRule.equality equalTo
            Validator.create message rule

        /// Value is not equal to provided value
        member _.notEquals (notEqualTo : 'a) (message : ValidationMessage) : Validator<'a> =                        
            let rule = ValidationRule.inequality notEqualTo
            Validator.create message rule    
                
    type ComparisonValidator<'a when 'a : comparison>() = 
        inherit EqualityValidator<'a>()

        /// Value is inclusively between provided min and max
        member _.between (min : 'a) (max : 'a) (message : ValidationMessage) : Validator<'a> =                        
            let rule = ValidationRule.between min max
            Validator.create message rule
        
        /// Value is greater than provided min
        member _.greaterThan (min : 'a) (message : ValidationMessage) : Validator<'a> =                        
            let rule = ValidationRule.greaterThan min
            Validator.create message rule
        
        /// Value is less than provided max
        member _.lessThan (max : 'a) (message : ValidationMessage) : Validator<'a> =                                    
            let rule = ValidationRule.lessThan max
            Validator.create message rule
    
    type StringValidator() =
        inherit EqualityValidator<string>() 

        /// Validate string is between length (inclusive)
        member _.betweenLen (min : int) (max : int) (message : ValidationMessage) : Validator<string> =            
            let rule = ValidationRule.betweenLen min max
            Validator.create message rule

        /// Validate string is null or ""
        member _.empty (message : ValidationMessage) : Validator<string> =            
            Validator.create message String.IsNullOrWhiteSpace

        /// Validate string length is equal to provided value
        member _.equalsLen (len : int) (message : ValidationMessage) : Validator<string> =            
            let rule = ValidationRule.equalsLen len
            Validator.create message rule

        /// Validate string length is greater than provided value
        member _.greaterThanLen (min : int) (message : ValidationMessage) : Validator<string> =            
            let rule = ValidationRule.greaterThanLen min
            Validator.create message rule

        /// Validate string length is less than provided value
        member _.lessThanLen (max : int) (message : ValidationMessage) : Validator<string> =            
            let rule = ValidationRule.lessThanLen max
            Validator.create message rule

        /// Validate string is not null or ""
        member _.notEmpty (message : ValidationMessage) : Validator<string> =            
            Validator.create message (fun str -> not(String.IsNullOrWhiteSpace (str)))

        /// Validate string matches regular expression
        member _.pattern (pattern : string) (message : ValidationMessage) : Validator<string> =            
            let rule = ValidationRule.pattern pattern
            Validator.create message rule

    type GuidValidator() =
        inherit EqualityValidator<Guid> ()

        /// Validate string is null or ""
        member _.empty (message : ValidationMessage) : Validator<Guid> =            
            Validator.create message (fun guid -> Guid.Empty = guid)

        /// Validate string is not null or ""
        member _.notEmpty (message : ValidationMessage) : Validator<Guid> =            
            Validator.create message (fun guid -> Guid.Empty <> guid)


    /// DateTime validators
    let DateTime = ComparisonValidator<DateTime>()
    
    /// DateTimeOffset validators
    let DateTimeOffset = ComparisonValidator<DateTimeOffset>()

    /// decimal validators
    let Decimal = ComparisonValidator<decimal>()

    /// float validators
    let Float = ComparisonValidator<float>()

    /// System.Guid validators
    let Guid = GuidValidator()

    /// int32 validators
    let Int = ComparisonValidator<int>()    

    /// int16 validators
    let Int16 = ComparisonValidator<int16>()

    /// int64 validators
    let Int64 = ComparisonValidator<int64>()

    /// string validators
    let String = StringValidator()

    /// System.TimeSpan validators
    let TimeSpan = ComparisonValidator<TimeSpan>()

    module Default = 
        type DefaultEqualityValidator<'a when 'a : equality>(x : EqualityValidator<'a>) =        
            /// Value is equal to provided value with the default error message
            member _.equals (equalTo: 'a) : Validator<'a> = x.equals equalTo (fun field -> sprintf "%s must be equal to %A" field equalTo)
        
            /// Value is not equal to provided value with the default error message
            member _.notEquals (notEqualTo : 'a) = x.notEquals notEqualTo (fun field -> sprintf "%s must not equal %A" field notEqualTo)
        
        type DefaultComparisonValidator<'a when 'a : comparison>(x : ComparisonValidator<'a>) = 
            inherit DefaultEqualityValidator<'a>(x)
    
            /// Value is inclusively between provided min and max with the default error message
            member _.between (min : 'a) (max : 'a) = x.between min max (fun field -> sprintf "%s must be between %A and %A" field min max)
                    
            /// Value is greater than provided min with the default error message
            member _.greaterThan (min : 'a) = x.greaterThan min (fun field -> sprintf "%s must be greater than or equal to %A" field min)
    
            /// Value is less than provided max with the default error message
            member _.lessThan (max : 'a) = x.lessThan max (fun field -> sprintf "%s must be less than or equal to %A" field max)
    
        type DefaultStringValidator(this : StringValidator) =
            inherit DefaultEqualityValidator<string>(this) 

            /// Validate string is between length (inclusive) with the default error message
            member _.betweenLen (min : int) (max : int) = this.betweenLen min max (fun field -> sprintf "%s must be between %i and %i characters" field min max)

            /// Validate string is null or "" with the default error message
            member _.empty = this.empty (fun field -> sprintf "%s must be empty" field)

            /// Validate string length is greater than provided value with the default error message
            member _.equalsLen (len : int) = this.equalsLen len (fun field -> sprintf "%s must be %i characters" field len)

            /// Validate string length is greater than provided value with the default error message
            member _.greaterThanLen (min : int) = this.greaterThanLen min (fun field -> sprintf "%s must not execeed %i characters" field min)

            /// Validate string length is less than provided value with the default error message
            member _.lessThanLen (max : int) = this.lessThanLen max (fun field -> sprintf "%s must be at least %i characters" field max)

            /// Validate string is not null or "" with the default error message
            member _.notEmpty = this.notEmpty (fun field -> sprintf "%s must not be empty" field)

            /// Validate string matches regular expression with the default error message
            member _.pattern (pattern : string) = this.pattern pattern (fun field -> sprintf "%s must match pattern %s" field pattern)
    
        type DefaultGuidValidator(this : GuidValidator) =
            inherit DefaultEqualityValidator<Guid>(this)

            /// Validate System.Guid is null or "" with the default error message
            member _.empty = this.empty (fun field -> sprintf "%s must be empty" field)

            /// Validate System.Guid is not null or "" with the default error message
            member _.notEmpty = this.notEmpty (fun field -> sprintf "%s must not be empty" field)

        /// Execute validator if 'a is Some, otherwise return Failure with the default error message
        let required (validator : Validator<'a>) (field : string) (value : 'a option) : ValidationResult<'a> =  
            required validator (fun field -> sprintf "%s is required" field) field value

        /// DateTime validators with the default error messages
        let DateTime = DefaultComparisonValidator<DateTime>(DateTime)
        
        /// DateTimeOffset validators with the default error messages
        let DateTimeOffset = DefaultComparisonValidator<DateTimeOffset>(DateTimeOffset)
        
        /// decimal validators with the default error messages
        let Decimal = DefaultComparisonValidator<decimal>(Decimal)
        
        /// float validators with the default error messages
        let Float = DefaultComparisonValidator<float>(Float)
        
        /// int32 validators with the default error messages
        let Int = DefaultComparisonValidator<int>(Int) 
        
        /// int16 validators with the default error messages
        let Int16 = DefaultComparisonValidator<int16>(Int16)
        
        /// int64 validators with the default error messages
        let Int64 = DefaultComparisonValidator<int64>(Int64)
        
        /// string validators with the default error messages
        let String = DefaultStringValidator(String)
        
        /// System.TimeSpan validators with the default error messages
        let TimeSpan = DefaultComparisonValidator<TimeSpan>(TimeSpan)


// ------------------------------------------------
// Operators
// ------------------------------------------------

/// Custom operators for ValidationResult
module Operators =
    /// Alias for ValidationResult.apply
    let inline (<*>) f x = ValidationResult.apply f x

    /// Alias for ValidationResult.map
    let inline (<!>) f x = ValidationResult.map f x

    /// Alias for ValidationResult.bind    
    let inline (>>=) x f = ValidationResult.bind f x

    /// Alias for Validator.compose
    let inline (<+>) v1 v2 = Validator.compose v1 v2


// ------------------------------------------------
// Builder
// ------------------------------------------------

/// Computation expression for ValidationResult<_>.
type ValidationResultBuilder() =
    member _.Return (value) : ValidationResult<'a> = Success value

    member _.ReturnFrom (result) : ValidationResult<'a> = result

    member _.Delay(fn) : unit -> ValidationResult<'a> = fn

    member _.Run(fn) : ValidationResult<'a> = fn ()
    
    member _.Bind (result, binder) = ValidationResult.bind binder result

    member x.Zero () = x.Return ()

    member x.TryWith (result, exceptionHandler) = 
        try x.ReturnFrom (result)        
        with ex -> exceptionHandler ex

    member x.TryFinally (result, fn) = 
        try x.ReturnFrom (result)        
        finally fn ()

    member x.Using (disposable : #IDisposable, fn) = 
        x.TryFinally(fn disposable, fun _ -> 
            match disposable with 
            | null -> () 
            | disposable -> disposable.Dispose()) 

    member x.While (guard,  fn) =
        if not (guard()) 
            then x.Zero () 
        else 
            do fn () |> ignore
            x.While(guard, fn)

    member x.For (items : seq<_>, fn) = 
        x.Using(items.GetEnumerator(), fun enum ->
            x.While(enum.MoveNext, 
                x.Delay (fun () -> fn enum.Current)))

    member x.Combine (result, fn) = 
        x.Bind(result, fun () -> fn ())

    member _.MergeSources (r1 : ValidationResult<'a>, r2 : ValidationResult<'b>) : ValidationResult<'a * 'b> =
        ValidationResult.zip r1 r2

    member _.BindReturn (result : ValidationResult<'a>, fn : 'a -> 'b) : ValidationResult<'b> =
        ValidationResult.map fn result

/// Validate computation expression
let validate = ValidationResultBuilder()
