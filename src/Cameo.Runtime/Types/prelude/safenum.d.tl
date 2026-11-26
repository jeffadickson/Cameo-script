-- SharpSafeNumerics type declarations for TealVM & Cameo.
-- Provides overflow-checked integers, SIMD batch addition, Q32.32 Fixed64, and IEEE Decimal64/Decimal128.

global record safenum
    enum ArithmeticError
        "none"
        "overflow"
        "underflow"
        "divide_by_zero"
        "out_of_range"
        "inexact"
        "non_finite"
    end

    enum ArithmeticPolicy
        "reject"
        "saturate"
        "wrap"
        "throw"
    end

    enum FixedRounding
        "exact"
        "to_even"
        "to_zero"
        "floor"
        "ceiling"
        "away_from_zero"
    end

    enum DecimalRounding
        "exact"
        "to_even"
    end

    record BatchAddResult
        values: List<integer>
        errors: List<string>
        vectorWidth: integer
    end

    record Fixed64
        userdata
        raw: function(Fixed64): integer
        toNumber: function(Fixed64): number
        add: function(Fixed64, Fixed64): Fixed64
        sub: function(Fixed64, Fixed64): Fixed64
        mul: function(Fixed64, Fixed64, ? FixedRounding): Fixed64
        div: function(Fixed64, Fixed64, ? FixedRounding): Fixed64
        neg: function(Fixed64): Fixed64
        tryAdd: function(Fixed64, Fixed64): Fixed64, ArithmeticError
        trySub: function(Fixed64, Fixed64): Fixed64, ArithmeticError
        tryMul: function(Fixed64, Fixed64, ? FixedRounding): Fixed64, ArithmeticError
        tryDiv: function(Fixed64, Fixed64, ? FixedRounding): Fixed64, ArithmeticError
        tryNeg: function(Fixed64): Fixed64, ArithmeticError

        metamethod __add: function(Fixed64, Fixed64): Fixed64
        metamethod __sub: function(Fixed64, Fixed64): Fixed64
        metamethod __mul: function(Fixed64, Fixed64): Fixed64
        metamethod __div: function(Fixed64, Fixed64): Fixed64
        metamethod __unm: function(Fixed64): Fixed64
        metamethod __lt: function(Fixed64, Fixed64): boolean
        metamethod __le: function(Fixed64, Fixed64): boolean
        metamethod __eq: function(Fixed64, Fixed64): boolean
        metamethod __tostring: function(Fixed64): string
    end

    record Decimal64
        userdata
        toNumber: function(Decimal64): number
        toDec128: function(Decimal64): Decimal128
        add: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64
        sub: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64
        mul: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64
        div: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64
        neg: function(Decimal64): Decimal64
        tryAdd: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64, ArithmeticError
        trySub: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64, ArithmeticError
        tryMul: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64, ArithmeticError
        tryDiv: function(Decimal64, Decimal64, ? DecimalRounding): Decimal64, ArithmeticError
        tryNeg: function(Decimal64): Decimal64, ArithmeticError

        metamethod __add: function(Decimal64, Decimal64): Decimal64
        metamethod __sub: function(Decimal64, Decimal64): Decimal64
        metamethod __mul: function(Decimal64, Decimal64): Decimal64
        metamethod __div: function(Decimal64, Decimal64): Decimal64
        metamethod __unm: function(Decimal64): Decimal64
        metamethod __lt: function(Decimal64, Decimal64): boolean
        metamethod __le: function(Decimal64, Decimal64): boolean
        metamethod __eq: function(Decimal64, Decimal64): boolean
        metamethod __tostring: function(Decimal64): string
    end

    record Decimal128
        userdata
        toNumber: function(Decimal128): number
        toDec64: function(Decimal128, ? DecimalRounding): Decimal64
        add: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128
        sub: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128
        mul: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128
        div: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128
        neg: function(Decimal128): Decimal128
        tryAdd: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128, ArithmeticError
        trySub: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128, ArithmeticError
        tryMul: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128, ArithmeticError
        tryDiv: function(Decimal128, Decimal128, ? DecimalRounding): Decimal128, ArithmeticError
        tryNeg: function(Decimal128): Decimal128, ArithmeticError

        metamethod __add: function(Decimal128, Decimal128): Decimal128
        metamethod __sub: function(Decimal128, Decimal128): Decimal128
        metamethod __mul: function(Decimal128, Decimal128): Decimal128
        metamethod __div: function(Decimal128, Decimal128): Decimal128
        metamethod __unm: function(Decimal128): Decimal128
        metamethod __lt: function(Decimal128, Decimal128): boolean
        metamethod __le: function(Decimal128, Decimal128): boolean
        metamethod __eq: function(Decimal128, Decimal128): boolean
        metamethod __tostring: function(Decimal128): string
    end

    intAdd: function(integer, integer, ? ArithmeticPolicy): integer, ArithmeticError
    intSub: function(integer, integer, ? ArithmeticPolicy): integer, ArithmeticError
    intMul: function(integer, integer, ? ArithmeticPolicy): integer, ArithmeticError
    intDiv: function(integer, integer): integer, ArithmeticError
    intNeg: function(integer): integer, ArithmeticError
    intInRange: function(integer, integer, integer): integer, ArithmeticError
    intAddBatch: function({integer} | List<integer>, {integer} | List<integer>): BatchAddResult
    batchVectorWidth: function(): integer

    fixedFromInt: function(integer): Fixed64, ArithmeticError
    fixedFromRatio: function(integer, integer, ? FixedRounding): Fixed64, ArithmeticError
    fixedFromRaw: function(integer): Fixed64

    dec64: function(string | number): Decimal64
    dec128: function(string | number): Decimal128
end
