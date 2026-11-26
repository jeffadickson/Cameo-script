-- The checker supplies the expected Result type for direct constructor calls.
global record Result<T, E>
   userdata
   fromPair: function<U, F>(U, F): Result<U, F>
   ok: function<U, F>(U): Result<U, F>
   err: function<U, F>(F): Result<U, F>
   isOk: function(self): boolean
   isErr: function(self): boolean
   value: function(self): T
   issue: function(self): E
   unwrapOr: function(self, T): T
   map: function<U>(self, function(T): U): Result<U, E>
   mapErr: function<F>(self, function(E): F): Result<T, F>
end

global ok <const>: function(any): userdata
global err <const>: function(any): userdata
