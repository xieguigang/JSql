# JSql

一个**实验性质的 SQL 引擎**：用 VB.NET 编写，以 **JSON 文件**作为数据库存储，通过**命令行 REPL** 交互式执行 SQL 语句。

- 一个**文件夹 = 一个数据库**，文件夹里的**一个 JSON 文件 = 一张数据表**
- 内置完整的 SQL 前端（手写词法/语法分析器 + 执行引擎），兼容 MySQL 常用查询语法
- 数据表支持**索引**：复用 [GCModeller LINQ](https://github.com/xieguigang/GCModeller) 项目中的内存索引算法（哈希索引、范围索引、全文索引），索引可**落盘为 `.idx` 文件**并在重启后恢复
- 存储层通过接口抽象，**当前实现 JSON 格式**，后续可扩展 CSV 等格式

> 说明：这是一个实验项目，目标是把"能跑通的 SQL 引擎骨架"讲清楚，不追求与 MySQL 完全一致，也不提供事务、外键、视图、存储过程等高级特性。

---

## 快速开始

### 1. 构建

```bash
dotnet build src\JSql\JSql.vbproj
```

### 2. 启动 REPL

```bash
# 指定数据根目录（推荐）
dotnet run --project src\JSql\JSql.vbproj -- --db D:\data\jsql

# 也可以先构建后直接运行
dotnet src\JSql\bin\Debug\net10.0\JSql.exe --db D:\data\jsql
```

数据根目录的选取顺序：`--db <dir>` / `-d <dir>` / `--db=<dir>` → 环境变量 `JSQL_HOME` → 当前目录下的 `jsql-data`。目录不存在时会自动创建。

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

数据根目录下的每个子目录是一个数据库：

```
D:\data\jsql\
└── shop\                      <- 数据库 shop
    ├── orders.json            <- 数据表 orders（schema + 行数据）
    ├── users.json
    └── .indexes\              <- 该库的索引文件
        └── orders_amount_range.idx
```

表文件示例（`orders.json`）：

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
  ],
  "rows": [
    { "oid": 1, "customer": "alice", "amount": 120.5, "created": "2026-09-11 10:00:00" }
  ]
}
```

- 写表采用**临时文件 + 替换**的原子写入，避免进程中断把表文件写坏
- 注释（`COMMENT`）作为 schema 的一部分落盘，`DESCRIBE` 可以直接查看
- 索引文件位于库目录的 `.indexes` 子目录，命名规则 `<表>_<列>_<索引类型>.idx`，随表数据变更自动重建

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
DESCRIBE t;                -- 同 SHOW COLUMNS FROM t，输出 Field/Type/Null/Key/Default/Comment
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
│   ├── ITableStore.vb      # 存储抽象 + 类型规范化/值强转 + 按扩展名分发的工厂
│   ├── JsonTableStore.vb   # JSON 表存储（schema + 行数据，原子写回）
│   └── DatabaseCatalog.vb  # 库/表目录管理
└── Indexing\
    ├── JsonMemoryIndex.vb  # 继承 LINQ MemoryIndex 的 JSON 行集合适配器
    ├── IndexManager.vb     # 索引门面：建/删索引、WHERE 条件探测、写后重建
    └── IndexPersistence.vb # 索引落盘（*.idx）与加载
```

查询的执行顺序：`SQL 文本 → Tokenizer → SqlParser(AST) → Executor`，其中 `WHERE` 会先交给 `IndexManager` 探测可用索引得到候选行，再做表达式精确过滤（索引只做候选集收缩，正确性由表达式求值保证；索引异常时自动退回全表扫描）。

## 依赖

- .NET 10（VB.NET）
- 项目引用（无需额外安装 NuGet 包）：
  - `GCModeller\src\runtime\Darwinism\src\data\LINQ\LINQ\LINQ.vbproj`：提供 `MemoryIndex`、`TermHashIndex`、`RangeIndex`、`FTSEngine` 等索引算法
  - `sciBASIC#\Data\DataFrame`、`sciBASIC#\Microsoft.VisualBasic.Core`：基础库

## 已知限制

- 不支持事务、外键、视图、子查询、联合查询（`UNION`）、用户自定义函数与存储过程
- `LIKE` 不参与索引加速（全文索引为分词匹配，用于 `LIKE` 可能漏匹配，因此保持全表扫描以保证结果正确）
- 表级 `UNIQUE KEY` / `KEY` 仅作为 schema 元数据记录，不会自动生成物理索引、也不做唯一性校验；需要索引请用 `CREATE INDEX`
- `AUTO_INCREMENT` 语法会被接受但不生成自增序号，需自行指定主键值
- 每次写操作为整表重写，适合小表与实验场景；单进程 REPL，无并发写入保护

## License

MIT，详见 [LICENSE](LICENSE)。
