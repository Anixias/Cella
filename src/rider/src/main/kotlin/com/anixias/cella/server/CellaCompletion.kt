package com.anixias.cella.server

import com.anixias.cella.CellaLanguage
import com.intellij.codeInsight.AutoPopupController
import com.intellij.codeInsight.completion.CompletionContributor
import com.intellij.codeInsight.completion.CompletionParameters
import com.intellij.codeInsight.completion.CompletionResultSet
import com.intellij.codeInsight.completion.InsertHandler
import com.intellij.codeInsight.completion.InsertionContext
import com.intellij.codeInsight.completion.PrioritizedLookupElement
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.codeInsight.lookup.LookupElement
import com.intellij.codeInsight.lookup.LookupElementBuilder
import com.intellij.codeInsight.template.TemplateManager
import com.intellij.codeInsight.template.impl.ConstantNode
import com.intellij.icons.AllIcons
import com.intellij.lang.parameterInfo.CreateParameterInfoContext
import com.intellij.lang.parameterInfo.ParameterInfoHandler
import com.intellij.lang.parameterInfo.ParameterInfoUIContext
import com.intellij.lang.parameterInfo.UpdateParameterInfoContext
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import org.eclipse.lsp4j.CompletionItem
import org.eclipse.lsp4j.CompletionItemKind
import org.eclipse.lsp4j.CompletionList
import org.eclipse.lsp4j.CompletionParams
import org.eclipse.lsp4j.InsertTextFormat
import org.eclipse.lsp4j.SignatureHelpParams
import org.eclipse.lsp4j.SignatureInformation
import org.eclipse.lsp4j.TextDocumentIdentifier
import org.eclipse.lsp4j.jsonrpc.messages.Either
import javax.swing.Icon

class CellaCompletionContributor : CompletionContributor() {
	override fun fillCompletionVariants(parameters: CompletionParameters, result: CompletionResultSet) {
		val editor = parameters.editor
		val project = editor.project ?: return
		val file = parameters.originalFile.virtualFile ?: return
		val server = CellaServer.get(project)
		val position = server.position(editor.document, parameters.offset)
		val future = server.request {
			it.textDocumentService.completion(CompletionParams(TextDocumentIdentifier(CellaServer.uriOf(file)), position))
		}
		
		var list: Either<List<CompletionItem>, CompletionList>? = null
		val deadline = System.currentTimeMillis() + 3000
		while (list == null && System.currentTimeMillis() < deadline) {
			ProgressManager.checkCanceled()
			list = server.await(future, 50)
			if (future.isDone && list == null)
				return
		}
		
		val items = list?.let { if (it.isLeft) it.left else it.right.items } ?: return
		for (item in items)
			result.addElement(toLookup(item))
	}
	
	private fun toLookup(item: CompletionItem): LookupElement {
		val detail = item.detail.orEmpty()
		val signature = detail.substringAfter(": ", "")
		var builder = LookupElementBuilder.create(item.label)
			.withIcon(iconOf(item.kind))
			.withTailText(if (signature.isEmpty()) null else " $signature", true)
			.withBoldness(item.kind == CompletionItemKind.Keyword)
		
		val text = item.insertText ?: item.label
		if (item.insertTextFormat == InsertTextFormat.Snippet)
			builder = builder.withInsertHandler(SnippetInsertHandler(text))
		
		val rank = item.sortText?.firstOrNull()?.digitToIntOrNull() ?: 2
		return PrioritizedLookupElement.withPriority(builder, (10 - rank).toDouble())
	}
	
	private fun iconOf(kind: CompletionItemKind?): Icon? = when (kind) {
		CompletionItemKind.Method -> AllIcons.Nodes.Method
		CompletionItemKind.Function -> AllIcons.Nodes.Function
		CompletionItemKind.Field -> AllIcons.Nodes.Field
		CompletionItemKind.Variable -> AllIcons.Nodes.Variable
		CompletionItemKind.Class, CompletionItemKind.Struct -> AllIcons.Nodes.Class
		CompletionItemKind.Interface -> AllIcons.Nodes.Interface
		CompletionItemKind.Module -> AllIcons.Nodes.Package
		CompletionItemKind.Property -> AllIcons.Nodes.Property
		CompletionItemKind.Enum -> AllIcons.Nodes.Enum
		CompletionItemKind.EnumMember -> AllIcons.Nodes.Field
		CompletionItemKind.Constant -> AllIcons.Nodes.Constant
		CompletionItemKind.TypeParameter -> AllIcons.Nodes.Type
		else -> null
	}
}

class SnippetInsertHandler(private val snippet: String) : InsertHandler<LookupElement> {
	private val placeholder = Regex("""\$\{(\d+)}|\$0""")
	
	override fun handleInsert(context: InsertionContext, item: LookupElement) {
		val editor = context.editor
		context.document.deleteString(context.startOffset, context.tailOffset)
		editor.caretModel.moveToOffset(context.startOffset)
		
		val manager = TemplateManager.getInstance(context.project)
		val template = manager.createTemplate("", "")
		template.isToReformat = false
		var position = 0
		for (match in placeholder.findAll(snippet)) {
			template.addTextSegment(snippet.substring(position, match.range.first))
			val number = match.groupValues[1]
			if (number.isEmpty())
				template.addEndVariable()
			else
				template.addVariable("P$number", ConstantNode(""), ConstantNode(""), true)
			
			position = match.range.last + 1
		}
		
		template.addTextSegment(snippet.substring(position))
		manager.startTemplate(editor, template)
	}
}

class CellaParameterInfoHandler : ParameterInfoHandler<PsiElement, SignatureInformation> {
	override fun findElementForParameterInfo(context: CreateParameterInfoContext): PsiElement? {
		val project = context.project
		val file = context.file.virtualFile ?: return null
		val server = CellaServer.get(project)
		val position = server.position(context.editor.document, context.offset)
		val help = server.await(server.request {
			it.textDocumentService.signatureHelp(SignatureHelpParams(TextDocumentIdentifier(CellaServer.uriOf(file)), position))
		}, 2000) ?: return null
		
		if (help.signatures.isNullOrEmpty())
			return null
		
		context.itemsToShow = help.signatures.toTypedArray()
		return findCallStart(context.file, context.editor, context.offset)
	}
	
	override fun showParameterInfo(element: PsiElement, context: CreateParameterInfoContext) =
		context.showHint(element, element.textRange.startOffset, this)
	
	override fun findElementForUpdatingParameterInfo(context: UpdateParameterInfoContext): PsiElement? =
		findCallStart(context.file, context.editor, context.offset)
	
	override fun updateParameterInfo(element: PsiElement, context: UpdateParameterInfoContext) {
		if (element.textRange.startOffset != context.parameterOwner?.textRange?.startOffset) {
			context.removeHint()
			return
		}
		
		context.setCurrentParameter(argumentIndex(context.editor.document.charsSequence, element.textRange.startOffset,
			context.offset))
	}
	
	override fun updateUI(signature: SignatureInformation, context: ParameterInfoUIContext) {
		val label = signature.label
		val parameter = signature.parameters?.getOrNull(context.currentParameterIndex)?.label
		val range = when {
			parameter == null -> -1 to -1
			parameter.isRight -> parameter.right.first to parameter.right.second
			else -> label.indexOf(parameter.left).let { it to it + parameter.left.length }
		}
		
		context.setupUIComponentPresentation(label, range.first, range.second, false, false, false,
			context.defaultParameterColor)
	}
	
	private fun findCallStart(file: PsiFile, editor: Editor, offset: Int): PsiElement? {
		val open = findOpenParenthesis(editor.document.charsSequence, offset) ?: return null
		return file.findElementAt(open)
	}
	
	private fun findOpenParenthesis(text: CharSequence, offset: Int): Int? {
		var depth = 0
		for (index in minOf(offset, text.length) - 1 downTo 0) {
			when (text[index]) {
				')', ']', '}' -> depth++
				'(' -> if (depth == 0) return index else depth--
				'[', '{' -> if (depth == 0) return null else depth--
			}
		}
		
		return null
	}
	
	private fun argumentIndex(text: CharSequence, open: Int, offset: Int): Int {
		var depth = 0
		var index = 0
		for (position in open + 1 until minOf(offset, text.length)) {
			when (text[position]) {
				'(', '[', '{' -> depth++
				')', ']', '}' -> depth--
				',' -> if (depth == 0) index++
			}
		}
		
		return index
	}
}

class CellaTypedHandler : TypedHandlerDelegate() {
	override fun checkAutoPopup(charTyped: Char, project: Project, editor: Editor, file: PsiFile): Result {
		if (file.language != CellaLanguage)
			return Result.CONTINUE
		
		when (charTyped) {
			'.' -> AutoPopupController.getInstance(project).scheduleAutoPopup(editor)
			'(' -> AutoPopupController.getInstance(project).autoPopupParameterInfo(editor, null)
		}
		
		return Result.CONTINUE
	}
}
