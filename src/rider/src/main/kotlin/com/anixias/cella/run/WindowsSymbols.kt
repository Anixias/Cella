package com.anixias.cella.run

import com.sun.jna.Library
import com.sun.jna.Memory
import com.sun.jna.Native
import com.sun.jna.Pointer
import com.sun.jna.WString
import com.sun.jna.ptr.LongByReference
import java.io.Closeable

class WindowsSymbols private constructor(private val process: Pointer) : Closeable {
	@Suppress("FunctionName")
	private interface DbgHelp : Library {
		fun SymSetOptions(options: Int): Int
		fun SymInitializeW(process: Pointer, searchPath: WString?, invadeProcess: Boolean): Boolean
		fun SymRefreshModuleList(process: Pointer): Boolean
		fun SymFromAddrW(process: Pointer, address: Long, displacement: LongByReference, symbol: Pointer): Boolean
		fun SymCleanup(process: Pointer): Boolean
	}

	@Suppress("FunctionName")
	private interface Kernel32 : Library {
		fun OpenProcess(access: Int, inheritHandle: Boolean, processId: Int): Pointer?
		fun CloseHandle(handle: Pointer): Boolean
	}

	companion object {
		private const val QueryInformationAndReadMemory = 0x0410
		private const val FailCriticalErrors = 0x200
		private const val SymbolInfoSize = 88
		private const val NameLengthOffset = 76
		private const val MaxNameLengthOffset = 80
		private const val NameOffset = 84
		private const val TagOffset = 72
		private const val DataTag = 7
		private const val MaxNameLength = 1024

		private val lock = Any()
		private val dbgHelp: DbgHelp by lazy { Native.load("dbghelp", DbgHelp::class.java) }
		private val kernel32: Kernel32 by lazy { Native.load("kernel32", Kernel32::class.java) }

		fun open(processId: Long, searchPath: String): WindowsSymbols? = synchronized(lock) {
			val process = kernel32.OpenProcess(QueryInformationAndReadMemory, false, processId.toInt()) ?: return null
			dbgHelp.SymSetOptions(FailCriticalErrors)
			if (!dbgHelp.SymInitializeW(process, WString(searchPath), true)) {
				kernel32.CloseHandle(process)
				return null
			}

			WindowsSymbols(process)
		}
	}

	private val symbol = Memory((SymbolInfoSize + 2 * MaxNameLength).toLong())

	fun dataNameAt(address: Long): String? = synchronized(lock) {
		find(address) ?: if (dbgHelp.SymRefreshModuleList(process)) find(address) else null
	}

	private fun find(address: Long): String? {
		symbol.clear()
		symbol.setInt(0, SymbolInfoSize)
		symbol.setInt(MaxNameLengthOffset.toLong(), MaxNameLength)
		val displacement = LongByReference()
		if (!dbgHelp.SymFromAddrW(process, address, displacement, symbol) || displacement.value != 0L ||
		    symbol.getInt(TagOffset.toLong()) != DataTag)
			return null

		return String(symbol.getCharArray(NameOffset.toLong(), symbol.getInt(NameLengthOffset.toLong())))
	}

	override fun close() = synchronized(lock) {
		dbgHelp.SymCleanup(process)
		kernel32.CloseHandle(process)
		Unit
	}
}
