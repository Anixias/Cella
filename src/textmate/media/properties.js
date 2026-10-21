(function () {
	const vscode = acquireVsCodeApi();
	const root = document.getElementById("root");
	const groups = [
		["Application", ["OutputType", "AssemblyName", "CellaVersion", "StandardLibrary", "Referenceable"]],
		["Build", ["BoundsChecks", "OverflowChecks", "DebugInfo", "SystemLinkPreference"]],
		["Imports", ["Imports", "Prelude"]]
	];

	let settings;
	let versions = [];
	let scope = null;
	let pending = [];

	window.addEventListener("message", event => {
		const message = event.data;
		if (message.type === "settings") {
			settings = message.settings;
			versions = message.versions ?? [];
			render();
		} else if (message.type === "error") {
			root.replaceChildren(element("div", "errors", message.message));
		} else if (message.type === "condition") {
			if (!conditions().includes(message.condition))
				pending.push(message.condition);

			scope = message.condition;
			render();
		}
	});

	vscode.postMessage({ type: "ready" });

	function element(tag, className, text) {
		const node = document.createElement(tag);
		if (className)
			node.className = className;

		if (text !== undefined)
			node.textContent = text;

		return node;
	}

	function option(select, value, text, selected) {
		const node = element("option", null, text);
		node.value = value;
		node.selected = selected;
		select.append(node);
	}

	function button(text, action) {
		const node = element("button", null, text);
		node.onclick = action;
		return node;
	}

	function conditions() {
		const known = settings.conditions.map(entry => entry.condition);
		return known.concat(pending.filter(condition => !known.includes(condition)));
	}

	function valuesOf(condition) {
		return condition === null
			? settings.values
			: settings.conditions.find(entry => entry.condition === condition)?.values ?? {};
	}

	function send(edits) {
		vscode.postMessage({ type: "edit", edits });
	}

	function post(edit) {
		send([scope === null ? edit : { ...edit, condition: scope }]);
	}

	function describe(value) {
		if (value === true)
			return "On";

		if (value === false)
			return "Off";

		return Array.isArray(value) ? value.join(", ") : value;
	}

	function render() {
		root.replaceChildren();
		if (scope !== null && !conditions().includes(scope))
			scope = null;

		root.append(header());
		if (settings.errors.length > 0) {
			const errors = element("div", "errors");
			for (const error of settings.errors)
				errors.append(element("div", null, error));

			root.append(errors);
			return;
		}

		const grouped = new Set(groups.flatMap(([, names]) => names));
		for (const [title, names] of groups) {
			const extra = title === "Application"
				? settings.settings.filter(setting => !grouped.has(setting.name) &&
					!["flags", "lints", "references"].includes(setting.kind)).map(setting => setting.name)
				: [];

			section(title, names.concat(extra));
		}

		flags();
		lints();
		references();
	}

	function header() {
		const bar = element("div", "header");
		bar.append(element("label", null, "Applies to"));
		const select = element("select");
		option(select, "", "All builds", scope === null);
		for (const condition of conditions())
			option(select, `c:${condition}`, condition, scope === condition);

		option(select, "+", "Add condition…", false);
		select.onchange = () => {
			if (select.value === "+") {
				vscode.postMessage({ type: "addCondition" });
				render();
				return;
			}

			scope = select.value === "" ? null : select.value.slice(2);
			render();
		};

		bar.append(select);
		if (scope !== null)
			bar.append(button("Remove", () => {
				const removed = scope;
				pending = pending.filter(condition => condition !== removed);
				scope = null;
				if (settings.conditions.some(entry => entry.condition === removed))
					send([{ removeCondition: removed }]);
				else
					render();
			}));

		bar.append(button("Open as Text", () => vscode.postMessage({ type: "openText" })));
		return bar;
	}

	function section(title, names) {
		const rows = names.map(name => settings.settings.find(setting => setting.name === name)).filter(Boolean);
		if (rows.length === 0)
			return;

		const block = element("section");
		block.append(element("h2", null, title));
		const grid = element("div", "grid");
		for (const setting of rows) {
			const label = element("label", null, setting.label);
			const control = field(setting);
			control.id = `setting-${setting.name}`;
			label.htmlFor = control.id;
			grid.append(label, control);
		}

		block.append(grid);
		root.append(block);
	}

	function field(setting) {
		const top = settings.values[setting.name];
		const editable = scope === null || setting.conditional;
		const value = scope !== null && setting.conditional ? valuesOf(scope)[setting.name] : top;
		const inherited = describe(top) ?? setting.default;
		const fallback = scope === null
			? setting.default != null ? `Default (${setting.default})` : null
			: inherited != null ? `Inherited (${inherited})` : "Inherited";

		if (setting.kind === "choice" || setting.kind === "boolean") {
			const select = element("select");
			select.disabled = !editable;
			const choices = setting.kind === "boolean"
				? [[true, "On"], [false, "Off"]]
				: setting.choices.map(choice => [choice, choice]);

			if (fallback !== null)
				option(select, "", fallback, value === undefined);

			choices.forEach(([choice, text], index) => option(select, String(index), text, value === choice));
			select.onchange = () => post({
				setting: setting.name,
				value: select.value === "" ? null : choices[Number(select.value)][0]
			});

			return select;
		}

		if (setting.kind === "textList") {
			const area = element("textarea");
			area.rows = 4;
			area.disabled = !editable;
			area.placeholder = fallback ?? "";
			area.value = (value ?? []).join("\n");
			area.onchange = () => {
				const lines = area.value.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
				post({ setting: setting.name, value: lines.length > 0 ? lines : null });
			};

			return area;
		}

		const input = element("input");
		input.disabled = !editable;
		input.placeholder = fallback ?? "";
		input.value = value ?? "";
		if (setting.kind === "version") {
			const list = element("datalist");
			list.id = "versions";
			for (const version of versions)
				option(list, version, version, false);

			root.append(list);
			input.setAttribute("list", "versions");
		}

		input.onchange = () => post({ setting: setting.name, value: input.value.trim() || null });
		return input;
	}

	function flags() {
		const block = element("section");
		block.append(element("h2", null, "Flags"));
		const editable = scope === null;
		const table = element("div", "rows");
		for (const [name, value] of Object.entries(settings.flags)) {
			const row = element("div", "row");
			const check = element("input");
			check.type = "checkbox";
			check.checked = value;
			check.disabled = !editable;
			check.onchange = () => send([{ flag: name, value: check.checked }]);
			const remove = button("Remove", () => send([{ flag: name, value: null }]));
			remove.disabled = !editable;
			row.append(check, element("span", "name", name), remove);
			table.append(row);
		}

		const add = element("div", "row");
		const name = element("input");
		name.disabled = !editable;
		const create = button("Add", () => {
			if (name.value.trim())
				send([{ flag: name.value.trim(), value: true }]);
		});

		create.disabled = !editable;
		add.append(name, create);
		table.append(add);
		block.append(table);
		root.append(block);
	}

	function lints() {
		const levels = settings.settings.find(setting => setting.kind === "lints")?.choices ?? [];
		const levelsOf = condition => condition === null
			? settings.lintLevels
			: settings.conditions.find(entry => entry.condition === condition)?.lintLevels ?? {};
		
		const block = element("section");
		block.append(element("h2", null, "Lints"));
		const table = element("div", "rows");
		for (const lint of settings.lints) {
			const row = element("div", "row");
			const select = element("select");
			const inherited = scope === null ? lint.default : levelsOf(null)[lint.name] ?? lint.default;
			const current = levelsOf(scope)[lint.name];
			option(select, "", scope === null ? `Default (${inherited})` : `Inherited (${inherited})`, !current);
			for (const level of levels)
				option(select, level, level, current === level);
			
			select.onchange = () => post({ lint: lint.name, value: select.value || null });
			row.append(element("span", "name", lint.label), select);
			table.append(row);
		}
		
		block.append(table);
		root.append(block);
	}
	
	function references() {
		const block = element("section");
		block.append(element("h2", null, "References"));
		const table = element("div", "rows");
		for (const reference of settings.references.filter(entry => (entry.condition ?? null) === scope)) {
			const row = element("div", "row");
			const alias = element("input");
			alias.placeholder = "Alias";
			alias.value = reference.alias ?? "";
			alias.onchange = () => post({ reference: reference.index, alias: alias.value.trim() || null });
			const name = reference.path.split("/").pop().replace(/\.celp$/, "");
			row.append(element("span", reference.exists ? "name" : "name missing", name),
				element("span", "path", reference.path), alias,
				button("Remove", () => post({ removeReference: reference.index })));

			table.append(row);
		}

		table.append(button("Add…", () => vscode.postMessage({ type: "addReference", condition: scope })));
		block.append(table);
		root.append(block);
	}
})();
