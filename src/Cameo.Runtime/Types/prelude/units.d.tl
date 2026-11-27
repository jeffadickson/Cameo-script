-- SharpUnits type declarations for TealVM & Cameo.
-- Enforces ISO 80000 affine point-vs-displacement safety at compile time.

global record SharpUnits
    record SymbolicUnit
        userdata
        symbol: string
        ascii_symbol: string
        scale_to_si: number

        pow: function(SymbolicUnit, integer): SymbolicUnit
        root: function(SymbolicUnit, integer): SymbolicUnit
        to_si: function(SymbolicUnit): SymbolicUnit

        metamethod __mul: function(SymbolicUnit, SymbolicUnit): SymbolicUnit
        metamethod __div: function(SymbolicUnit, SymbolicUnit): SymbolicUnit
        metamethod __eq: function(SymbolicUnit, SymbolicUnit): boolean
        metamethod __tostring: function(SymbolicUnit): string
    end

    record Quantity
        userdata
        value: number
        si_value: number
        unit: SymbolicUnit
        spec: string

        in_unit: function(Quantity, string | SymbolicUnit): Quantity
        to_si: function(Quantity): Quantity
        format: function(Quantity, ? string): string
        is_equivalent: function(Quantity, Quantity, ? number): boolean

        metamethod __add: function(Quantity, Quantity): Quantity
        metamethod __sub: function(Quantity, Quantity): Quantity
        metamethod __unm: function(Quantity): Quantity
        metamethod __mul: function(Quantity | number, Quantity | number): Quantity
        metamethod __div: function(Quantity | number, Quantity | number): Quantity
        metamethod __lt: function(Quantity, Quantity): boolean
        metamethod __le: function(Quantity, Quantity): boolean
        metamethod __eq: function(Quantity, Quantity): boolean
        metamethod __tostring: function(Quantity): string
    end

    record QuantityPoint
        userdata
        value: number
        unit: SymbolicUnit
        origin: string

        in_unit: function(QuantityPoint, string | SymbolicUnit): QuantityPoint
        relative_to: function(QuantityPoint, string): QuantityPoint
        shift: function(QuantityPoint, Quantity): QuantityPoint
        to_celsius: function(QuantityPoint): QuantityPoint
        to_fahrenheit: function(QuantityPoint): QuantityPoint
        to_kelvin: function(QuantityPoint): QuantityPoint

        -- Compile-time affine safety:
        --   Point + Quantity  -> QuantityPoint (valid shift)
        --   Point - Point     -> Quantity      (valid displacement)
        --   Point + Point     -> COMPILE ERROR!
        metamethod __add: function(QuantityPoint, Quantity): QuantityPoint
        metamethod __sub: function(QuantityPoint, QuantityPoint): Quantity
        metamethod __sub: function(QuantityPoint, Quantity): QuantityPoint
        metamethod __lt: function(QuantityPoint, QuantityPoint): boolean
        metamethod __le: function(QuantityPoint, QuantityPoint): boolean
        metamethod __eq: function(QuantityPoint, QuantityPoint): boolean
        metamethod __tostring: function(QuantityPoint): string
    end

    record QuantitySeries
        userdata
        count: integer
        unit: SymbolicUnit

        in_unit: function(QuantitySeries, string | SymbolicUnit): QuantitySeries
        auto_scale: function(QuantitySeries): QuantitySeries
        relative_to: function(QuantitySeries, string): QuantitySeries
        min: function(QuantitySeries): Quantity
        max: function(QuantitySeries): Quantity
        mean: function(QuantitySeries): Quantity
        rms: function(QuantitySeries): Quantity
        peak_to_peak: function(QuantitySeries): Quantity
        at: function(QuantitySeries, integer): Quantity
        to_list: function(QuantitySeries): List<number>

        metamethod __len: function(QuantitySeries): integer
        metamethod __tostring: function(QuantitySeries): string
    end

    -- Named compile-time dimension aliases (all interoperable with Quantity<"unit">)
    type Resistance = Quantity<"Ohm">
    type Voltage = Quantity<"V">
    type Capacitance = Quantity<"F">
    type Inductance = Quantity<"H">
    type Current = Quantity<"A">
    type Conductance = Quantity<"S">
    type Charge = Quantity<"C">
    type Power = Quantity<"W">
    type Energy = Quantity<"J">
    type Frequency = Quantity<"Hz">
    type Time = Quantity<"s">
    type Length = Quantity<"m">
    type Area = Quantity<"m^2">
    type Volume = Quantity<"m^3">
    type Mass = Quantity<"kg">
    type Force = Quantity<"N">
    type Pressure = Quantity<"Pa">
    type Temperature = Quantity<"K">
    type Velocity = Quantity<"m/s">
    type Acceleration = Quantity<"m/s^2">
    type Dimensionless = Quantity<"1">

    unit: function(string): SymbolicUnit
    qty: function(number, string | SymbolicUnit): Quantity
    parse: function(string): Quantity
    point: function(number, string, ? string): QuantityPoint
    series: function({number}, string, ? string): QuantitySeries
    series: function(List<number>, string, ? string): QuantitySeries
    series: function(FloatArray, string, ? string): QuantitySeries
end

global units: SharpUnits
