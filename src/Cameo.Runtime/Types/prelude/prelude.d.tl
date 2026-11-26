-- From the Teal compiler (https://github.com/teal-language/tl), teal/default/.
-- Copyright (c) Hisham Muhammad and contributors; MIT license, see THIRD-PARTY-NOTICES.md.
-- These are definitions that are core to the language.
--
-- In Lua terms, these are definitions that fundamental to the
-- Lua language behavior itself, that is, which are valid
-- even before one does luaL_openlibs to load the standard
-- library.

global interface any
end

global interface thread
end

global interface userdata
   is userdata
end

global record metatable<T>
   enum Mode
      "k" "v" "kv"
   end

   __call: function(T, any...): any...
   __mode: Mode
   __name: string
   __tostring: function(T): string
   __pairs: function<K, V>(T): function(): (K, V)

   __index: any --[[FIXME: function | table | anything with an __index metamethod]]
   __newindex: any --[[FIXME: function | table | anything with an __index metamethod]]

   __gc: function(T)
   __close: function(T)

   __add: function<A, B, C>(A, B): C
   __sub: function<A, B, C>(A, B): C
   __mul: function<A, B, C>(A, B): C
   __div: function<A, B, C>(A, B): C
   __idiv: function<A, B, C>(A, B): C
   __mod: function<A, B, C>(A, B): C
   __pow: function<A, B, C>(A, B): C
   __band: function<A, B, C>(A, B): C
   __bor: function<A, B, C>(A, B): C
   __bxor: function<A, B, C>(A, B): C
   __shl: function<A, B, C>(A, B): C
   __shr: function<A, B, C>(A, B): C
   __concat: function<A, B, C>(A, B): C

   __len: function<A>(T): A
   __unm: function<A>(T): A
   __bnot: function<A>(T): A

   __eq: function<A, B>(A, B): boolean
   __lt: function<A, B>(A, B): boolean
   __le: function<A, B>(A, B): boolean
end

-- used by `class` declarations; not meant to be called directly
global __class: function(any, any, ? any, ? any, ? any, ? any, ? string, ? {string:any})
-- whether a value is an object of the class with this identity (what `value is Class` calls)
global __isa: function(any, string): boolean
-- whether a value is an object of a class (or of a class derived from it, or using it as a mixin)
global instanceof: function(any, any): boolean
-- used by Cameo record declarations with inline methods; not meant to be called directly
global __cameo_record: function(any, any, string, ? string)
global __cameo_bless: function(any, any): any
