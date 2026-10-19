package com.anixias.cella.server

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CellaScanner
import com.anixias.cella.lexer.CellaToken
import com.intellij.codeInsight.AutoPopupController
import com.intellij.codeInsight.completion.CompletionContributor
import com.intellij.codeInsight.completion.CompletionParameters
import com.intellij.codeInsight.completion.CompletionResultSet
import com.intellij.codeInsight.completion.InsertHandler
import com.intellij.codeInsight.completion.CompletionSorter
import com.intellij.codeInsight.completion.InsertionContext
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.codeInsight.lookup.LookupElement
import com.intellij.codeInsight.lookup.LookupElementBuilder
import com.intellij.codeInsight.lookup.LookupElementWeigher
import com.intellij.codeInsight.template.TemplateManager
import com.intellij.codeInsight.template.impl.ConstantNode
import com.intellij.icons.AllIcons
import com.intellij.lang.parameterInfo.CreateParameterInfoContext
import com.intellij.lang.parameterInfo.ParameterInfoHandler
import com.intellij.lang.parameterInfo.ParameterInfoUIContext
import com.intellij.lang.parameterInfo.UpdateParameterInfoContext
import com.intellij.openapi.editor.Editor
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
		
		val list = server.await(future, 3000) ?: return
		val items = if (list.isLeft) list.left else list.right.items
		val sorted = result.withRelevanceSorter(CompletionSorter.defaultSorter(parameters, result.prefixMatcher)
			.weighBefore("priority", SortTextWeigher))
		
		for (item in items)
			sorted.addElement(toLookup(item))
	}
	
	private fun toLookup(item: CompletionItem): LookupElement {
		var builder = LookupElementBuilder.create(item, item.label)
			.withIcon(iconOf(item.kind))
			.withTailText(item.labelDetails?.detail, true)
			.withTypeText(item.labelDetails?.description, true)
			.withBoldness(item.kind == CompletionItemKind.Keyword)
		
		val text = item.insertText ?: item.label
		if (item.insertTextFormat == InsertTextFormat.Snippet)
			builder = builder.withInsertHandler(SnippetInsertHandler(text))
		
		return builder
	}
	
	private object SortTextWeigher : LookupElementWeigher("cellaSortText") {
		override fun weigh(element: LookupElement): Comparable<*> =
			(element.`object` as? CompletionItem)?.sortText ?: element.lookupString
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
	
	private fun tokensBefore(text: CharSequence, offset: Int): List<Pair<CellaToken, Int>> {
		val scanner = CellaScanner()
		scanner.start(text, 0, minOf(offset, text.length), CellaScanner.NORMAL)
		val tokens = mutableListOf<Pair<CellaToken, Int>>()
		while (true) {
			val type = scanner.tokenType ?: break
			tokens.add(type to scanner.tokenStart)
			scanner.advance()
		}
		
		return tokens
	}
	
	private fun findOpenParenthesis(text: CharSequence, offset: Int): Int? {
		var depth = 0
		for ((type, start) in tokensBefore(text, offset).asReversed()) {
			when (type) {
				CellaToken.RIGHT_PAREN, CellaToken.RIGHT_BRACKET, CellaToken.RIGHT_BRACE -> depth++
				CellaToken.LEFT_PAREN -> if (depth == 0) return start else depth--
				CellaToken.LEFT_BRACKET, CellaToken.LEFT_BRACE -> if (depth == 0) return null else depth--
				else -> {}
			}
		}
		
		return null
	}
	
	private fun argumentIndex(text: CharSequence, open: Int, offset: Int): Int {
		var depth = 0
		var index = 0
		for ((type, start) in tokensBefore(text, offset)) {
			if (start <= open)
				continue
			
			when (type) {
				CellaToken.LEFT_PAREN, CellaToken.LEFT_BRACKET, CellaToken.LEFT_BRACE -> depth++
				CellaToken.RIGHT_PAREN, CellaToken.RIGHT_BRACKET, CellaToken.RIGHT_BRACE -> depth--
				CellaToken.COMMA -> if (depth == 0) index++
				else -> {}
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
