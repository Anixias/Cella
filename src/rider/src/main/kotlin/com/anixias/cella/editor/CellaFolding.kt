package com.anixias.cella.editor

import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.lang.ASTNode
import com.intellij.lang.folding.FoldingBuilderEx
import com.intellij.lang.folding.FoldingDescriptor
import com.intellij.openapi.editor.Document
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiElement

class CellaFoldingBuilder : FoldingBuilderEx(), DumbAware {
	override fun buildFoldRegions(root: PsiElement, document: Document, quick: Boolean): Array<FoldingDescriptor> {
		val descriptors = ArrayList<FoldingDescriptor>()
		val open = ArrayDeque<ASTNode>()
		val pending = ArrayDeque<ASTNode>().apply { addLast(root.node) }
		while (pending.isNotEmpty()) {
			val node = pending.removeLast()
			if (node.firstChildNode != null) {
				generateSequence(node.lastChildNode, ASTNode::getTreePrev).forEach(pending::addLast)
				continue
			}

			when (CellaTokenTypes.tokenOf(node.elementType)) {
				CellaToken.LEFT_BRACE -> open.addLast(node)
				CellaToken.RIGHT_BRACE -> {
					val brace = open.removeLastOrNull() ?: continue
					val range = TextRange(brace.startOffset, node.startOffset + node.textLength)
					if (document.getLineNumber(range.startOffset) != document.getLineNumber(range.endOffset))
						descriptors += FoldingDescriptor(brace, range)
				}

				else -> {}
			}
		}

		return descriptors.toTypedArray()
	}

	override fun getPlaceholderText(node: ASTNode) = "{...}"

	override fun isCollapsedByDefault(node: ASTNode) = false
}
