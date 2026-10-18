package com.anixias.cella.run

import com.intellij.openapi.util.Key
import com.intellij.openapi.util.Pair
import com.intellij.xdebugger.frame.XCompositeNode
import com.intellij.xdebugger.frame.XFullValueEvaluator
import com.jetbrains.cidr.execution.debugger.backend.DebuggerDriver
import com.jetbrains.cidr.execution.debugger.backend.EvaluationContext
import com.jetbrains.cidr.execution.debugger.backend.LLValue
import com.jetbrains.cidr.execution.debugger.backend.LLValueData
import com.jetbrains.cidr.execution.debugger.evaluation.CidrPhysicalValue
import com.jetbrains.cidr.execution.debugger.evaluation.ValueRendererFactory
import com.jetbrains.cidr.execution.debugger.evaluation.renderers.ValueRenderer
import com.jetbrains.cidr.execution.debugger.memory.AddressRange

private val strDataKey = Key.create<Boolean>("cella.str.data")

class CellaValueRendererFactory : ValueRendererFactory {
	override fun createRenderer(context: ValueRendererFactory.FactoryContext): ValueRenderer? = when {
		context.llValue.type == "str" -> StrRenderer(context.physicalValue)
		context.llValue.getUserData(strDataKey) == true -> AddressRenderer(context.physicalValue)
		else -> null
	}
}

private class AddressRenderer(value: CidrPhysicalValue) : ValueRenderer(value) {
	override fun doComputeValueAndEvaluator(context: EvaluationContext, value: LLValue, data: LLValueData):
		Pair<String, XFullValueEvaluator?> = data.pointerOrNull?.let { Pair.create<String, XFullValueEvaluator?>(it, null) }
		?: super.doComputeValueAndEvaluator(context, value, data)
}

private class StrRenderer(value: CidrPhysicalValue) : ValueRenderer(value) {
	companion object {
		private const val MaxLength = 4096

		private val driverField = runCatching {
			EvaluationContext::class.java.getDeclaredField("myDriver").apply { isAccessible = true }
		}.getOrNull()
	}

	override fun doComputeValueAndEvaluator(context: EvaluationContext, value: LLValue, data: LLValueData):
		Pair<String, XFullValueEvaluator?> {
		val driver = driverField?.get(context) as? DebuggerDriver
		val children = context.getVariableChildren(value, 0, 2).list
		val length = children.firstOrNull { it.name == "length" }?.let { context.getData(it).value.toLongOrNull() }
		val pointer = children.firstOrNull { it.name == "data" }?.let { context.getData(it).pointerAddress }
		if (driver == null || length == null || pointer == null)
			return super.doComputeValueAndEvaluator(context, value, data)

		val shown = length.coerceIn(0, MaxLength.toLong())
		val bytes = if (shown == 0L)
			ByteArray(0)
		else
			driver.dumpMemory(AddressRange(pointer, pointer.plus(shown - 1)))
				.flatMap { it.bytes }
				.take(shown.toInt())
				.toByteArray()

		val text = quote(String(bytes, Charsets.UTF_8))
		return Pair.create(if (shown < length) "$text…" else text, null)
	}

	override fun addChildrenTo(children: List<LLValue>, context: EvaluationContext, node: XCompositeNode, from: Int,
		isLast: Boolean) {
		children.firstOrNull { it.name == "data" }?.putUserData(strDataKey, true)
		super.addChildrenTo(children, context, node, from, isLast)
	}
	
	private fun quote(text: String) = buildString {
		append('"')
		for (character in text) {
			when (character) {
				'"' -> append("\\\"")
				'\\' -> append("\\\\")
				'\n' -> append("\\n")
				'\r' -> append("\\r")
				'\t' -> append("\\t")
				'\u0000' -> append("\\0")
				else -> append(character)
			}
		}

		append('"')
	}
}
