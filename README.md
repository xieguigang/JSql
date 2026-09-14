# JSql

一个**实验性质的 SQL 引擎**：用 VB.NET 编写，以 **文本文件**作为数据库存储，通过**命令行 REPL** 交互式执行 SQL 语句。

- 一个**文件夹 = 一个数据库**，文件夹里的**一个数据文件 = 一张数据表**
- 支持两种行存储格式：**JSONL**（一行一个 JSON 对象，默认）与 **CSV**（表头行 + 数据行），可用启动开关 `--format <jsonl|csv>` 切换新建表使用的格式
- 内置完整的 SQL 前端（手写词法/语法分析器 + 执行引擎），兼容 MySQL 常用查询语法
- 数据表支持**索引**：复用 [GCModeller LINQ](https://github.com/xieguigang/GCModeller) 项目中的内存索引算法（哈希索引、范围索引、全文索引），索引可**落盘为 `.idx` 文件**并在重启后恢复
- 存储层通过 **`IDbFileStorageProvider`** 接口抽象，可在启动时切换物理文件引擎：
  - **文本后端**（`TextFileStorage`，默认）：底层是与行格式无关的纯文本行存储引擎（`TextLineStore` + WAL），行编解码器（JSONL / CSV）可插拔
  - **SQLite 后端**（`SqliteStorage`，独立项目 `src\Sqlite`）：以 **SQLite 数据库文件**持久化，一个 JSql 数据库 = 一个 `.sqlite` 文件（内含该库的所有表），直接读写 SQLite 文件格式（无需原生 sqlite 引擎）

> 说明：这是一个实验项目，目标是把"能跑通的 SQL 引擎骨架"讲清楚，不追求与 MySQL 完全一致，也不提供事务、外键、视图、存储过程等高级特性。

---

## 快速开始

### 1. 构建

```bash
dotnet build src\Repl\Repl.vbproj
```

### 2. 启动 REPL

```bash
# 指定数据根目录（推荐）
dotnet run --project src\Repl\Repl.vbproj -- --db D:\data\jsql

# 也可以先构建后直接运行
dotnet src\Repl\bin\x64\Debug\net10.0\Repl.dll --db D:\data\jsql

# 使用 SQLite 文件后端（一个数据库 = 一个 <库>.sqlite 文件）
dotnet run --project src\Repl\Repl.vbproj -- --db D:\data\jsql --backend sqlite
```

数据根目录的选取顺序：`--db <dir>` / `-d <dir>` / `--db=<dir>` → 环境变量 `JSQL_HOME` → 当前目录下的 `jsql-data`。目录不存在时会自动创建。

存储相关开关（可选）：

| 开关 | 默认 | 说明 |
|---|---|---|
| `--backend <text\|sqlite>` | `text` | 物理存储后端（别名 `--engine`）：`text` = JSONL/CSV 文件夹布局；`sqlite` = 一个数据库一个 `.sqlite` 文件 |
| `--merge-idle <秒>` | `30` | 空闲多久后把 WAL 合并回数据文件；`0` 关闭后台合并 |
| `--merge-after <n>` | `2000` | 单表未合并操作数达到 n 时提前合并 |
| `--no-fsync` | 默认 | 每条语句结束时 flush 一次日志（写入快，断电可能丢最后一条语句） |
| `--fsync` | | 每次写操作都 fsync 日志（慢一些，断电级安全） |
| `--legacy-json` | | 新建表仍使用旧版单文件 `.json` 布局（便于对比） |
| `--format <jsonl\|csv>` | `jsonl` | 新建表使用的行存储格式（别名 `--storage`，也支持 `--format=csv`） |
| `--multiprocess` | 关闭 | 多进程访问模式（别名 `--share`）：每条语句结束即释放表锁，多个进程可交替访问同一数据库 |
| `--lock-timeout <ms>` | `5000` | 多进程模式下等待表锁的超时（毫秒），超时后报错 |
| `--lock-fail-fast` | | 多进程模式下锁冲突立即失败，不等待 |
| `--no-merge-on-release` | | 多进程模式下释放锁时不合并 WAL（写入吞吐优先） |
| `--lock-mode <exclusive\|shared\|none>` | `exclusive` | 进程级锁模式：`shared` 供只读进程共存，`none` 不加锁 |
| `--verbose` | | 把存储层诊断（索引重建、撕裂尾修复、checkpoint）打印到 stderr |

### 3. 第一次使用

```sql
jsql> CREATE DATABASE shop;
jsql> USE shop;
jsql> CREATE TABLE orders (
   ->   oid INT PRIMARY KEY,
   ->   customer VARCHAR(40) NOT NULL COMMENT 'customer name',
   ->   amount DOUBLE NOT NULL,
   ->   created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
   -> ) COMMENT='order records';
jsql> INSERT INTO orders (oid, customer, amount) VALUES (1, 'alice', 120.5), (2, 'bob', 80.0);
jsql> SELECT * FROM orders WHERE amount > 100;
+-----+----------+--------+---------------------+
| oid | customer | amount | created             |
+-----+----------+--------+---------------------+
| 1   | alice    | 120.5  | 2026-09-11 10:00:00 |
+-----+----------+--------+---------------------+
1 row(s) in set
```

REPL 规则：语句以 `;` 结尾，未输入分号时可**多行续行**（提示符变为 `    -> `）；支持 `help`、`clear`、`quit` 等元命令。

---

## 数据是怎么存的

数据根目录下的每个子目录是一个数据库。每张表的 **schema 与数据分开存放**：schema 写入 `<表>.schema.json`，行数据写入数据文件，二者都由底层的 **行存储引擎（稀疏行索引 + WAL 写前日志）** 管理。数据文件可以是 **JSONL**（一行一个 JSON 对象，默认）或 **CSV**（第 1 行表头 + 后续数据行），打开时按文件扩展名自动识别：

```
D:\data\jsql\
└── shop\                            <- 数据库 shop
    ├── orders.schema.json           <- 表结构（列类型、注释、表级键）
    ├── orders.jsonl                 <- JSONL 行数据：每行一条 JSON 记录
    ├── orders.jsonl.wal             <- 写前日志（未合并的增删改）
    ├── orders.jsonl.idx             <- 数据文件的稀疏行索引
    ├── orders.jsonl.lock            <- 进程独占锁
    ├── customers.schema.json
    ├── customers.csv                <- CSV 行数据：首行表头，其后每行一条记录
    ├── customers.csv.wal            <- 对应 CSV 表的写前日志
    ├── customers.csv.idx
    ├── customers.csv.lock
    └── .indexes\                    <- 列索引（哈希/范围/全文）
        └── orders_amount_range.idx
```

> CSV 表同样拥有一份独立的 `<表>.schema.json`——CSV 文件只承载表头与行数据（不含列类型、NOT NULL、键、注释），这些元数据保存在 schema 文件中，`DESCRIBE` 读的就是它。列类型缺失的信息不会被推断。

schema 文件示例（`orders.schema.json`）：

```json
{
  "table": "orders",
  "comment": "order records",
  "keys": [
    { "name": "PRIMARY", "columns": ["oid"], "unique": true, "primary": true }
  ],
  "columns": [
    { "name": "oid", "type": "INT", "notNull": true, "primaryKey": true, "defaultValue": null, "comment": null },
    { "name": "customer", "type": "VARCHAR(40)", "notNull": true, "primaryKey": false, "defaultValue": null,
      "comment": "customer name" }
  ]
}
```

数据文件示例（`orders.jsonl`，每行一条记录，行号即行序，从 1 开始）：

```jsonl
{"oid":1,"customer":"alice","amount":120.5,"created":"2026-09-11 10:00:00"}
{"oid":2,"customer":"bob","amount":80,"created":"2026-09-11 10:05:00"}
```

CSV 数据文件示例（`customers.csv`，第 1 行为表头，列序与 schema 一致）：

```csv
oid,customer,amount,created
1,alice,120.5,2026-09-11 10:00:00
2,bob,80,2026-09-11 10:05:00
```

CSV 单元格内的逗号、双引号会按 RFC 4180 规则用双引号包裹并转义；由于底层是**行式**引擎（一行一条记录），单元格内的换行符在写盘时会被规范化为空格。

### SQLite 后端（`--backend sqlite`）

与文本后端「一个文件夹 = 一个数据库」不同，SQLite 后端是**一个数据库 = 一个 `.sqlite` 文件**，库内所有表共存于该文件：

```
D:\data\jsql\
├── shop.sqlite                      <- 数据库 shop（SQLite 数据库文件，内含所有表）
├── app.sqlite                       <- 数据库 app
└── .jsql\                           <- 辅助目录（不会被列为数据库）
    ├── shop\
    │   ├── orders.schema.json       <- 表结构（列类型/注释/键等 DDL 无法承载的元数据）
    │   └── .indexes\                <- 列索引（*.idx）
    └── app\
        ├── users.schema.json
        └── .indexes\
            └── users_name_hash.idx
```

- 行数据写入 `shop.sqlite` 中的表，外部工具（含其它 SQLite 实现）可直接打开读取；JSql 的完整模式元数据（列类型原始文本、`COMMENT`、`DEFAULT`、表级 `UNIQUE KEY`/`KEY`）与列索引保存在辅助目录 `.jsql\<库>\`，因此 `DESCRIBE` / `SHOW INDEXES` / `SHOW STORAGE` / 列索引照常工作
- 类型映射：`INT→INTEGER`、`DOUBLE→FLOAT`、`VARCHAR→TEXT`、`BOOLEAN→BOOLEAN`、`DATE`/`DATETIME→TEXT`（保持 ISO 字符串原样往返）；`INT PRIMARY KEY` 映射为 SQLite 的 `INTEGER PRIMARY KEY`（rowid 别名）
- 写入沿用 `SqlEngine` 的 `SaveTable → 表会话 → Merge/CHECKPOINT/退出` 流程；因底层托管写入引擎采用「内存模型 + 提交时整文件重建」，**只在 `Merge` / `CHECKPOINT` / 空闲合并 / 退出时**把内存模型整体提交到磁盘
- 打开外部 SQLite 文件（缺少 `<表>.schema.json`）时，会从 `CREATE TABLE` DDL 反解列名与类型来构造表结构；`COMMENT`/`DEFAULT`/表级键等信息不可得
- `--format` / `--legacy-json` 等文本后端开关对 SQLite 后端无效

### 写入路径与 WAL

- `INSERT` → 数据文件末尾追加（一次日志记录 + 一次 flush）
- `UPDATE` → 只替换变化的行；`DELETE` → 只删除对应行区间
- 所有修改先写 WAL，再由底层引擎把挂起修改"虚拟挂载"到读取结果上，因此**写完立刻能读到**
- **空闲合并（checkpoint）**：引擎空闲超过一段时间（默认 30s）后自动把 WAL 合并回 `*.jsonl` 并清空日志；也可随时手动触发
- 进程崩溃后重新打开表时会**重放 WAL**，未合并的写入不会丢失

### 多进程访问（`--multiprocess`）

默认情况下，一张表的数据文件在打开期间持有**进程级独占锁**，因此同一个数据库同一时刻只能被一个进程使用。加上 `--multiprocess`（别名 `--share`）后改为**语句级锁**：每条 SQL 语句结束时释放表锁，其它进程即可取得锁执行自己的语句，从而支持多个进程（或多个 JSql 实例）**交替**访问同一个数据库。

```bash
# 两个终端可以同时使用同一个数据目录
dotnet run --project src\Repl\Repl.vbproj -- --db D:\data\jsql --multiprocess
dotnet run --project src\Repl\Repl.vbproj -- --db D:\data\jsql --multiprocess
```

行为与语义：

- **语句级原子**：单条语句内「取锁 → 读表 → 内存修改 → 写回 → 重建索引」全程持锁，语句之间不持锁；因此同一张表任一时刻仍只有一个写者，也不会出现跨语句的丢失更新
- **锁冲突策略**：默认等待重试（`--lock-timeout <ms>`，默认 5000ms，超时后给出明确错误）；`--lock-fail-fast` 改为立即失败
- **释放即合并**：默认在释放锁时把挂起的 WAL 合并回数据文件（`--no-merge-on-release` 可关闭，改为保留在 WAL 中由下次打开重放）；未合并的记录不会丢失
- **索引缓存失效**：释放锁时会清空查询索引的内存缓存，保证能看到其它进程新写入的行
- **诊断语句不加锁**：`DESCRIBE` / `SHOW COLUMNS` 直接读取 schema 文件，不需要表锁，因此不会被其它进程的写锁阻塞
- **锁粒度是表**：锁是每张表一个文件锁，因此不同进程可以同时操作同一个数据库的**不同表**；跨多表语句在极端加锁顺序下可能互相等待，此时由等待超时降级为可读错误

限制：

- 该模式只覆盖文本后端（JSONL / CSV）；`--legacy-json` 单文件整表布局与 `--backend sqlite` 仍不支持多进程
- 不提供多写并发与冲突检测（仍是「单表单写者」模型）
- 每次语句结束都会重新打开数据文件并（按需）重放 WAL，写入吞吐低于单进程模式，故默认关闭
- `--lock-mode shared` 供只读进程共存（多个读者可同时打开，写者与所有读者互斥）；读会话禁止写入

### 查看与维护

```sql
SHOW STORAGE [FROM t];   -- 行数 / 未合并操作数 / WAL 大小 / 数据文件大小 / 布局
CHECKPOINT [TABLE t];    -- 立刻把 WAL 合并回数据文件
```

- schema 采用**临时文件 + 替换**的原子写入；数据文件的合并由底层引擎保证崩溃安全（合并标记 + `.bak`）
- 注释（`COMMENT`）作为 schema 的一部分落盘，`DESCRIBE` 可以直接查看
- 索引文件位于库目录的 `.indexes` 子目录，命名规则 `<表>_<列>_<索引类型>.idx`，随表数据变更自动重建
- 旧版单文件 `<表>.json` 表**仍可读取**；第一次写入时会自动迁移为上述双文件布局，原文件保留为 `<表>.json.bak`

---

## 支持的 SQL

### 数据类型

| 书写 | 规范化类型 | 说明 |
|---|---|---|
| `INT / INTEGER / BIGINT / SMALLINT / TINYINT` | `INT` | 整数（64 位存储） |
| `DOUBLE / FLOAT / DECIMAL / NUMERIC / REAL` | `DOUBLE` | 浮点数 |
| `VARCHAR(n) / CHAR / TEXT` | `VARCHAR` | 字符串 |
| `DATE` | `DATE` | `yyyy-MM-dd` |
| `DATETIME / TIMESTAMP` | `DATETIME` | `yyyy-MM-dd HH:mm:ss` |
| `BOOLEAN / BOOL / BIT` | `BOOLEAN` | 显示与比较为 `1/0` |

### 数据定义（DDL）

```sql
CREATE DATABASE [IF NOT EXISTS] db;
CREATE TABLE [IF NOT EXISTS] t (
  id   INT NOT NULL PRIMARY KEY AUTO_INCREMENT,
  name VARCHAR(50) NOT NULL DEFAULT 'n/a' COMMENT 'the name',
  age  INT UNSIGNED,
  KEY idx_name (name),              -- 表级键（记录为元数据）
  UNIQUE KEY uk_name_age (name, age),
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb3 COMMENT='table comment';

CREATE INDEX idx_age  ON t (age);                 -- 自动按列类型选择
CREATE INDEX idx_age  ON t (age) USING BTREE;     -- 数值/日期范围索引
CREATE INDEX idx_name ON t (name) USING HASH;     -- 字符串等值索引
CREATE INDEX idx_txt  ON t (body) USING FULLTEXT; -- 全文索引

DROP DATABASE [IF EXISTS] db;
DROP TABLE [IF EXISTS] t;
DROP INDEX idx_name ON t;
```

- 索引类型：`HASH`（字符串等值）、`BTREE`/`RANGE`（整数、浮点、日期的比较与范围）、`FULLTEXT`（全文）
- 不指定 `USING` 时：数值/日期列默认建范围索引，字符串列默认建哈希索引
- 表级 `PRIMARY KEY (col)`、`UNIQUE KEY`、`KEY` 会写入 schema 元数据；表级 `PRIMARY KEY` 同时把列标记为 `PRI`

### 数据操作（DML）

```sql
INSERT INTO t (id, name, age) VALUES (1, 'alice', 30), (2, 'bob', 25);
INSERT INTO t VALUES (3, 'carol', 35);          -- 按建表顺序填入所有列

UPDATE t SET age = age + 1, name = 'Ann' WHERE id = 1;
DELETE FROM t WHERE age < 18;
```

- 默认值支持常量与 `CURRENT_TIMESTAMP` / `NOW()`
- `NOT NULL` 列写入 `NULL` 会报错
- 每次写表后该表的索引自动重建并重新落盘

### 查询（SELECT）

```sql
SELECT [DISTINCT] 表达式/列 [AS 别名] [, ...]
FROM t [[AS] 别名]
  [INNER|LEFT [OUTER]] JOIN t2 [[AS] 别名] ON 条件
[WHERE 条件]
[GROUP BY 表达式 [, ...]]
[HAVING 条件]
[ORDER BY 列|别名|序号 [ASC|DESC] [, ...]]
[LIMIT n [OFFSET m]]        -- 也支持 LIMIT m,n
```

支持的表达式与谓词：

- 比较：`= <> != < <= > >=`，`IS NULL` / `IS NOT NULL`
- 逻辑：`AND` `OR` `NOT`、括号优先级
- 集合：`<NOT> IN (v1, v2, ...)`、`<NOT> BETWEEN a AND b`
- 模糊：`<NOT> LIKE 'a%'`（`%` 任意串、`_` 单字符，大小写不敏感）
- 算术：`+ - * / %`
- 聚合：`COUNT(*)`、`COUNT/SUM/AVG/MIN/MAX(expr)`
- 标识符可用反引号包裹：`` `id` ``；SQL 中支持 `-- 行注释` 与 `/* 块注释 */`

示例：

```sql
SELECT customer, COUNT(*) AS cnt, SUM(amount) AS total
FROM orders
WHERE amount BETWEEN 10 AND 500 AND customer LIKE 'a%'
GROUP BY customer
HAVING cnt >= 1
ORDER BY total DESC
LIMIT 10 OFFSET 0;
```

### 元命令与查看语句

```sql
USE db;                    -- 切换数据库
SHOW DATABASES;
SHOW TABLES [FROM db];
SHOW INDEXES FROM t;       -- 列出该表的物理索引（*.idx）
SHOW STORAGE [FROM t];     -- 存储状态：行数 / 未合并 WAL 操作数 / 文件大小 / 布局
DESCRIBE t;                -- 同 SHOW COLUMNS FROM t，输出 Field/Type/Null/Key/Default/Comment
CHECKPOINT [TABLE t];      -- 把 WAL 合并回 JSONL 数据文件（退出时也会自动执行）
help | clear | quit        -- REPL 内置命令
```

---

## 代码结构

```
src\JSql\
├── Program.vb              # REPL 宿主：启动参数、主循环、元命令、表格化输出
├── Sql\
│   ├── Tokenizer.vb        # 词法分析：标识符/反引号、数字、字符串、运算符、注释
│   ├── Ast.vb              # AST 节点（六类语句 + 表达式）与 SqlError
│   └── SqlParser.vb        # 递归下降语法分析器（MySQL 语法子集）
├── Engine\
│   ├── SqlEngine.vb        # 引擎门面：SQL 文本 -> AST -> 结果集
│   ├── Executor.vb         # SELECT/DML/DDL 执行器
│   ├── ExpressionEvaluator.vb  # 表达式求值与聚合计算
│   └── ResultSet.vb        # 查询结果模型
├── Storage\
│   ├── ITableStore.vb          # 类型规范化/值强转 + 表模型 + 旧整表读写接口
│   ├── JsonTableStore.vb       # 旧版单文件 JSON 表（迁移期读取与 --legacy-json）
│   ├── ITableSession.vb        # 表会话接口（行级读写 / 同步 / 合并 / 状态）
│   ├── IRowCodec.vb            # 行编解码抽象（格式无关：行对象 <-> 单行文本）
│   ├── JsonRowCodec.vb         # JSONL 行编解码（委托 RowJson）
│   ├── CsvRowCodec.vb          # CSV 行编解码（HeaderSchema / CharsParser / RowObject.ToString）
│   ├── TextTableSession.vb     # 通用会话：行存储引擎 + 行编解码器，行级差异同步、WAL 合并
│   ├── JsonlTableSession.vb    # JSONL 会话（TextTableSession + JsonRowCodec 的薄封装）
│   ├── CsvTableSession.vb      # CSV 会话（TextTableSession + CsvRowCodec 的薄封装）
│   ├── RowJson.vb              # 行 <-> JSONL 编解码（按 schema 列序）
│   ├── SchemaStore.vb          # <表>.schema.json 原子读写与旧格式读取
│   ├── StorageLayout.vb        # 文件命名/发现规则（.jsonl/.csv 与 .wal/.idx/.lock 等伴生文件）
│   ├── StorageOptions.vb       # 存储开关（格式、合并间隔、fsync、诊断、兼容模式）
│   ├── TableSessionPool.vb     # 会话缓存与独占锁管理、统一合并/关闭
│   ├── IdleMergeScheduler.vb   # 空闲时后台 checkpoint（与语句执行互斥）
│   ├── IDbFileStorageProvider.vb # 存储后端抽象（数据库级）：库/表管理、会话、加载保存、诊断
│   └── TextFileStorage.vb      # 文本后端（原 DatabaseCatalog）：库/表目录管理、格式识别、迁移与清理
└── Indexing\
    ├── JsonMemoryIndex.vb  # 继承 LINQ MemoryIndex 的 JSON 行集合适配器
    ├── IndexManager.vb     # 索引门面：建/删索引、WHERE 条件探测、写后重建（面向 IDbFileStorageProvider）
    └── IndexPersistence.vb # 索引落盘（*.idx）与加载

src\Sqlite\                    # SQLite 存储后端（实现 IDbFileStorageProvider 的可插拔插件）
├── SqliteStorage.vb           # 一库一 <库>.sqlite 的存储后端：库/表管理、writer 生命周期、会话池
├── SqliteTableSession.vb      # 表会话：桥接托管 SQLite3 写入引擎（ReadRows/SyncRows/SaveSchema/Merge）
└── SqliteSchemaMapper.vb      # JSql <-> SQLite 的类型/模式互转、DDL 反解、行值往返归一

src\Repl\                      # REPL 宿主
└── Program.vb                 # 启动参数（--db / --format / --backend …）、主循环、元命令、表格化输出
```

查询的执行顺序：`SQL 文本 → Tokenizer → SqlParser(AST) → Executor`，其中 `WHERE` 会先交给 `IndexManager` 探测可用索引得到候选行，再做表达式精确过滤（索引只做候选集收缩，正确性由表达式求值保证；索引异常时自动退回全表扫描）。

写入的执行顺序：`Executor → IDbFileStorageProvider.SaveTable → ITableSession.SyncRows(行级差异) → 空闲/退出时 Merge()`。其中：

- 文本后端：`TextFileStorage → TextTableSession → TextLineStore(内存片段层 + WAL) → 合并回 jsonl/csv 数据文件`；数据文件与 WAL 的崩溃安全由底层引擎（合并标记 + `.bak` + 撕裂写回滚）保证
- SQLite 后端：`SqliteStorage → SqliteTableSession → Sqlite3TableWriter(内存模型) → 空闲/退出时 Sqlite3Writer.Commit()（整库文件重建）`

## 依赖

- .NET 10（VB.NET）
- 项目引用（无需额外安装 NuGet 包）：
  - `GCModeller\src\runtime\Darwinism\src\data\LINQ\LINQ\LINQ.vbproj`：提供 `MemoryIndex`、`TermHashIndex`、`RangeIndex`、`FTSEngine` 等索引算法
  - `sciBASIC#\Microsoft.VisualBasic.Core`：除基础库外还提供 `Data.Repository.TextLineStore`（与行格式无关的纯文本行存储引擎 + WAL；`JsonlStore` 为其兼容别名）；其 `TextStoreOptions` 提供进程级锁模式（独占 / 共享读 / 不加锁）与锁等待超时，用于多进程访问
  - `sciBASIC#\Data\DataFrame`：DataFrame 基础库
  - `sciBASIC#\Data\BinaryData\SQLite3\SQLite3.vbproj`（`src\Sqlite` 引用）：纯托管 SQLite 数据库文件读写引擎（`Sqlite3Writer` / `Sqlite3TableWriter` / `Sqlite3Database`），不依赖原生 sqlite

工程依赖方向（避免循环引用）：`JSql`（库，定义 `IDbFileStorageProvider`）← `Sqlite`（实现该接口）← `Repl` / `test`（宿主显式注入后端）。

## 已知限制

- 不支持事务、外键、视图、子查询、联合查询（`UNION`）、用户自定义函数与存储过程
- `LIKE` 不参与索引加速（全文索引为分词匹配，用于 `LIKE` 可能漏匹配，因此保持全表扫描以保证结果正确）
- 表级 `UNIQUE KEY` / `KEY` 仅作为 schema 元数据记录，不会自动生成物理索引、也不做唯一性校验；需要索引请用 `CREATE INDEX`
- `AUTO_INCREMENT` 语法会被接受但不生成自增序号，需自行指定主键值
- 默认**单进程独占**：同一张表的数据文件在打开期间持有独占锁，同一个数据库目录同时只能被一个 JSql 实例打开（退出时会自动 checkpoint 并释放锁）；需要多进程交替访问请加 `--multiprocess`，详见「多进程访问」一节
- CSV 是**行式**存储：单元格内的换行符在写盘时被规范化为空格（否则会破坏“一行一条记录”的行边界）；CSV 中的空单元格读取为 `NULL`，因此无法区分空字符串与 `NULL`
- CSV 文件的列顺序以 schema 列序为权威：读取外部 CSV 时按其表头做列名映射（列序可不同），保存时会把表头刷新为 schema 列序
- 已有表按其数据文件扩展名（`.jsonl` / `.csv`）自动识别，`--format` 只影响**新建表**的默认格式；同一个数据库目录内可以同时存在两种格式的表
- 持久性分级：默认 `--no-fsync`（每条语句 flush 一次日志，能防进程崩溃，断电可能丢最后一条语句）；需要断电级安全请加 `--fsync`
- 空闲合并（checkpoint）在后台执行，`SHOW STORAGE` 可以观察"未合并操作数"；`CHECKPOINT` 与退出流程都会强制合并
- 单表数据全部载入内存后参与查询与索引构建，适合中小规模数据；无并发写入保护
- SQLite 后端：一个数据库 = 一个 `.sqlite` 文件（含该库所有表）；底层托管写入引擎为「内存模型 + 提交时整文件重建」，**整个数据库需能放进内存**，且不支持二级索引/视图/触发器/WITHOUT ROWID/自定义排序规则（JSql 自身的列索引 `*.idx` 与内存查询不受影响）
- SQLite 后端：`INT PRIMARY KEY` 映射为 SQLite 的 `INTEGER PRIMARY KEY`（rowid 别名），因此该列的值应为非空且唯一的整数（与原生 SQLite 语义一致）；其余类型按 `INTEGER / FLOAT / BOOLEAN / TEXT` 映射
- SQLite 后端没有 WAL / `--fsync` 语义：数据在 `Merge` / `CHECKPOINT` / 空闲合并 / 退出时整体提交，进程崩溃可能丢失尚未提交的修改
- 文本后端与 SQLite 后端互不识别对方的文件布局：切换 `--backend` 即切换存储引擎，同名库在两种后端下是彼此独立的数据

## License

MIT，详见 [LICENSE](LICENSE)。
