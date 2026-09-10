---
name: jsql-comment-support
overview: 让 JSql 解释器支持 MySQL 的 COMMENT 语法（列级 `COMMENT '...'`、表级 `COMMENT='...'`），并把注释随 schema 落盘到 JSON 表文件中；同时补齐该 DDL 语句所需的相邻语法（`UNSIGNED`、`DEFAULT CURRENT_TIMESTAMP`、表级 PRIMARY KEY / UNIQUE KEY / KEY 定义、ENGINE/CHARSET/AUTO_INCREMENT 表选项），DESCRIBE/SHOW COLUMNS 输出 Comment 列以便查看。
todos:
  - id: extend-schema-model
    content: 扩展 schema 模型：ColumnDef/TableSchema 增加 Comment 与 Keys，新增 TableKeyInfo，CoerceValue 支持 CURRENT_TIMESTAMP
    status: completed
  - id: parser-comment
    content: 解析器支持列级 COMMENT、UNSIGNED、DEFAULT CURRENT_TIMESTAMP、表级 PRIMARY KEY/UNIQUE KEY/KEY
    status: completed
    dependencies:
      - extend-schema-model
  - id: persist-comment
    content: JsonTableStore 增加 comment/keys 字段并完成读写映射
    status: completed
    dependencies:
      - extend-schema-model
  - id: executor-view
    content: 执行器写入表注释/键，DESCRIBE 增加 Comment 列，更新 help 文本
    status: completed
    dependencies:
      - parser-comment
      - persist-comment
  - id: build-verify
    content: 编译并用用户给出的 annotation 建表语句端到端验证注释落盘与查看
    status: completed
    dependencies:
      - executor-view
---

## 产品概述

为 JSql 解释器补充 MySQL 的 `COMMENT` 语法支持，并把注释随表结构（schema）持久化到 JSON 表文件中，便于直接查看；同时补齐报错 DDL 语句中相邻的 MySQL 语法，使该建表语句可以完整执行。

## 核心功能

- **列级注释**：列定义中支持 `COMMENT 'text'`（`db_xref int unsigned NOT NULL COMMENT '...'`），注释写入该列的 schema 信息
- **表级注释**：表选项支持 `COMMENT='text'` 与 `COMMENT 'text'`，注释写入表的 schema 信息
- **注释落盘**：注释作为 schema 的一部分写入 `<db>/<table>.json`，旧表文件缺少注释字段时仍可正常读取（向后兼容）
- **方便查看**：`DESCRIBE <table>` / `SHOW COLUMNS FROM <table>` 增加 Comment 列，表注释在表结构信息中可见
- **配套语法补齐**（同一条 DDL 必需，否则仍会报错）：
- 列属性：`UNSIGNED`（保留到原始类型文本）、`DEFAULT CURRENT_TIMESTAMP`（插入时取当前时间）
- 表级约束：`PRIMARY KEY (col)`、`UNIQUE KEY name (cols)`、`KEY name (cols)`，解析并记录到 schema 的键信息中，不再抛"暂不支持"错误
- 表选项：`ENGINE=...`、`AUTO_INCREMENT=...`、`DEFAULT CHARSET=...` 等按 MySQL 习惯解析并跳过

## 技术栈

- 沿用现有：VB.NET、net10.0、`dotnet build src\JSql\JSql.vbproj`，无新增外部依赖
- 序列化沿用 `System.Text.Json`（`JsonTableStore` 中 DTO 小写属性名）；新增字段为可选，旧 JSON 反序列化为默认值

## 实现方案

在不改变现有分层（Sql / Engine / Storage）的前提下做增量扩展：

1. **Schema 模型扩展（`Storage/ITableStore.vb`）**

- `ColumnDef` 增加 `Comment As String`
- `TableSchema` 增加 `Comment As String` 与 `Keys As List(Of TableKeyInfo)`；新增 `TableKeyInfo`（`Name`、`Columns As List(Of String)`、`Unique As Boolean`、`Primary As Boolean`），用于承载表级 `PRIMARY KEY / UNIQUE KEY / KEY`
- `SqlTypes.CoerceValue` 增加：当默认值为字符串 `CURRENT_TIMESTAMP` / `NOW()` 且目标类型为 DATE/DATETIME 时返回当前时间（按 `FormatDate` 格式化），使 `DEFAULT CURRENT_TIMESTAMP` 在 INSERT 时可落值
- `TableSchema.Clone()` 同步复制 Comment/Keys

2. **解析器扩展（`Sql/SqlParser.vb`）**

- `ParseColumnDef`：类型后若遇 `UNSIGNED` 追加到 `RawType`（如 `int unsigned`）；属性循环新增 `COMMENT 'text'` 分支；`DEFAULT` 支持 `CURRENT_TIMESTAMP` / `NOW()` 标识符（存为字符串标记 `"CURRENT_TIMESTAMP"`）
- `ParseColumnDefinitions`：遇到 `PRIMARY`/`UNIQUE`/`KEY`/`INDEX` 时改为解析表级键定义（`PRIMARY KEY (cols)`、`[UNIQUE] KEY|INDEX [name] (cols)`）并记录，其余（`CONSTRAINT`/`FOREIGN`）维持明确报错；支持可选的 `USING BTREE` 等后缀跳过
- 新增 `ParseTableOptions`：识别并捕获 `COMMENT [=] 'text'`，其余 `ENGINE=`、`AUTO_INCREMENT=`、`DEFAULT CHARSET=`、`ROW_FORMAT=` 等按 `标识符 [= 值]` 跳过，替换原 `SkipTrailingOptions` 在 CREATE TABLE 处的调用

3. **持久化扩展（`Storage/JsonTableStore.vb`）**

- `JsonColumn` 增加 `comment`；`JsonTableFile` 增加 `comment` 与 `keys`
- 新增 `JsonKey`（`name`、`columns`、`unique`、`primary`）DTO
- `Read`/`Write` 双向映射 Comment/Keys；写入时未设置注释不产生额外噪声（空值不写或写 null 均可，保持与现有风格一致）

4. **执行器与查看（`Engine/Executor.vb`）**

- `ExecuteCreate` 的 CREATE TABLE 分支：把表注释与表级键写入新建的 `TableSchema`（表级 `PRIMARY KEY (col)` 同时把对应列标记为 `PrimaryKey`）
- `ExecuteShow` 的 Columns 分支输出列由 5 列扩为 6 列：`Field / Type / Null / Key / Default / Comment`

5. **REPL 帮助文本（`Program.vb`）**

- `help` 中 CREATE TABLE 说明补充 COMMENT 与表级 KEY 写法，保持文档与实现一致

## 实现注意事项

- 注释内容含单引号时需沿用词法层已有的 `''` 转义规则，无需额外处理
- 表级键仅作元数据记录，不自动创建 `*.idx` 索引文件，避免与既有 `CREATE INDEX` 行为冲突；后续可按需扩展为自动建索引
- 保持既有 API 与 JSON 字段向后兼容：新增字段均可选，旧表文件读到的注释为空、键列表为空
- 影响范围小：不触碰索引层与执行引擎其余逻辑

## 架构与目录（本次仅改动以下文件）

```
g:\JSql\src\JSql\
├── Sql\SqlParser.vb              # [MODIFY] 列级 COMMENT、DEFAULT CURRENT_TIMESTAMP、UNSIGNED、表级 KEY、表选项含表注释
├── Storage\
│   ├── ITableStore.vb            # [MODIFY] ColumnDef.Comment、TableSchema.Comment/Keys、新增 TableKeyInfo、CoerceValue 支持 CURRENT_TIMESTAMP
│   └── JsonTableStore.vb         # [MODIFY] DTO 增加 comment/keys 字段并做读写映射
├── Engine\Executor.vb            # [MODIFY] CREATE TABLE 写入注释/键；DESCRIBE 输出 Comment 列
└── Program.vb                    # [MODIFY] help 文本补充 COMMENT 语法说明
```